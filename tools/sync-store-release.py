"""Verify exact release artifacts and submit only a new RCT Store package pair."""

import argparse
import base64
import copy
import hashlib
import io
import json
import os
import re
import struct
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


PRODUCT_ID = "9PK9DMKPH68Q"
IDENTITY_NAME = "ShanxinLi.RemoteCommanderTray"
PUBLISHER = "CN=FCD37627-8F13-4157-933C-E729F9F08408"
API = f"https://manage.devcenter.microsoft.com/v1.0/my/applications/{PRODUCT_ID}"
ARCHITECTURES = {"x64": 0x8664, "arm64": 0xAA64}
SUBMITTED = {
    "CommitStarted", "PreProcessing", "Certification", "Release",
    "PendingPublication", "Publishing", "Published",
}


class StoreError(Exception):
    """An error safe to print without exposing service credentials or payloads."""


def check(condition, code):
    if not condition:
        raise StoreError(code)


def read_version(version):
    check(isinstance(version, str) and re.fullmatch(r"[1-9][0-9]*\.[0-9]+\.[0-9]+\.0", version),
          "invalid_store_version")
    parts = tuple(map(int, version.split(".")))
    check(all(part <= 65535 for part in parts), "invalid_store_version")
    return parts


def props_versions(path):
    root = ET.parse(path)
    app = root.findall("./PropertyGroup/Version")
    store = root.findall("./PropertyGroup/StorePackageVersion")
    check(len(app) == len(store) == 1, "ambiguous_release_versions")
    read_version(store[0].text)
    return app[0].text, store[0].text


def verify_tag(tag, props):
    app, _ = props_versions(props)
    check(re.fullmatch(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", tag)
          and tag == f"v{app}", "release_tag_version_mismatch")


def verify_artifacts(commit, version, root):
    """Validate producer receipts and actual MSIX headers before authenticating."""
    check(re.fullmatch(r"[0-9a-f]{40}", commit), "invalid_source_commit")
    check({p.name for p in root.iterdir()} == {f"Store-{a}" for a in ARCHITECTURES},
          "unexpected_store_artifacts")
    files = {}
    for arch, machine in ARCHITECTURES.items():
        directory = root / f"Store-{arch}"
        name = f"RemoteCommanderTray_{version}_{arch}.msix"
        check({p.name for p in directory.glob("*.msix")} == {name}, "unexpected_store_package")
        receipt = json.loads((directory / "build-info.json").read_text(encoding="utf-8-sig"))
        data = (directory / name).read_bytes()
        check(receipt.get("SHA256", "").lower() == hashlib.sha256(data).hexdigest()
              and receipt.get("Package") == name
              and receipt.get("Architecture") == arch
              and receipt.get("PackageVersion") == version
              and receipt.get("StoreId") == PRODUCT_ID
              and receipt.get("SourceCommit") == commit
              and receipt.get("WorkingTreeModified") is False
              and receipt.get("Signed") is False
              and receipt.get("RequiresStoreCertification") is True,
              "store_receipt_mismatch")
        with zipfile.ZipFile(io.BytesIO(data)) as package:
            names = package.namelist()
            check(names.count("AppxManifest.xml") == names.count("RemoteCommanderTray.exe") == 1
                  and "AppxSignature.p7x" not in names, "unexpected_msix_content")
            manifest = ET.fromstring(package.read("AppxManifest.xml"))
            identity = manifest.find("{*}Identity")
            check(identity is not None and all(identity.get(key) == value for key, value in {
                "Name": IDENTITY_NAME, "Publisher": PUBLISHER, "Version": version,
                "ProcessorArchitecture": arch,
            }.items()), "msix_identity_mismatch")
            exe = package.read("RemoteCommanderTray.exe")
            check(len(exe) >= 64 and exe[:2] == b"MZ", "invalid_msix_executable")
            offset = struct.unpack_from("<I", exe, 60)[0]
            check(offset + 6 <= len(exe) and exe[offset:offset + 4] == b"PE\0\0"
                  and struct.unpack_from("<H", exe, offset + 4)[0] == machine,
                  "msix_executable_architecture_mismatch")
        files[f"RemoteCommanderTray_{version}_{arch}_{commit}.msix"] = data
    return files


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *_args, **_kwargs):
        return None


class StoreClient:
    def __init__(self, env):
        tenant = env.get("AZURE_AD_TENANT_ID", "")
        app = env.get("AZURE_AD_APPLICATION_CLIENT_ID", "")
        secret = env.get("AZURE_AD_APPLICATION_SECRET", "")
        guid = r"[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12}"
        check(re.fullmatch(guid, tenant) and re.fullmatch(guid, app) and secret,
              "store_credentials_not_configured")
        self.opener = urllib.request.build_opener(NoRedirect())
        form = urllib.parse.urlencode({
            "grant_type": "client_credentials", "client_id": app, "client_secret": secret,
            "resource": "https://manage.devcenter.microsoft.com",
        }).encode()
        response, _ = self.request("POST", f"https://login.microsoftonline.com/{tenant}/oauth2/token",
                                   form, {"Content-Type": "application/x-www-form-urlencoded"})
        self.token = json.loads(response).get("access_token")
        check(isinstance(self.token, str) and bool(self.token), "store_authentication_failed")

    def request(self, method, url, data=None, headers=None):
        try:
            with self.opener.open(
                urllib.request.Request(url, data, headers or {}, method=method), timeout=120
            ) as response:
                return response.read(), response.headers
        except Exception:
            # Transport exceptions can contain credential-bearing URLs.
            raise StoreError("store_request_failed") from None

    def api(self, method="GET", suffix="", payload=None):
        data = json.dumps(payload).encode() if payload is not None else None
        response, _ = self.request(method, API + suffix, data, {
            "Authorization": f"Bearer {self.token}", "Content-Type": "application/json",
        })
        return json.loads(response)

    def upload(self, url, files):
        parsed = urllib.parse.urlsplit(url)
        check(parsed.scheme == "https" and
              re.fullmatch(r"[a-z0-9]+\.blob\.core\.windows\.net", parsed.netloc) and
              "sig" in urllib.parse.parse_qs(parsed.query), "invalid_store_upload_url")
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as bundle:
            for name, data in files.items():
                bundle.writestr(name, data)
        data = buffer.getvalue()
        md5 = base64.b64encode(hashlib.md5(data, usedforsecurity=False).digest()).decode()
        self.request("PUT", url, data, {
            "x-ms-blob-type": "BlockBlob", "Content-Type": "application/zip",
            "Content-MD5": md5, "x-ms-blob-content-md5": md5,
        })
        _, headers = self.request("HEAD", url)
        check(headers.get("Content-Length") == str(len(data)) and
              headers.get("Content-MD5") == md5, "store_upload_not_verified")


def submission_id(value):
    check(isinstance(value, str) and re.fullmatch(r"[0-9]+", value), "invalid_submission_id")
    return value


def app_state(client):
    app = client.api()
    check(app.get("id") == PRODUCT_ID and app.get("packageIdentityName") == IDENTITY_NAME and
          app.get("publisherName") == PUBLISHER, "store_app_identity_mismatch")
    published = submission_id((app.get("lastPublishedApplicationSubmission") or {}).get("id"))
    pending = app.get("pendingApplicationSubmission")
    return published, submission_id(pending.get("id")) if pending else None


def inventory(client):
    published, pending = app_state(client)
    release = client.api(suffix=f"/submissions/{published}")
    check(release.get("status") == "Published", "published_submission_not_ready")
    versions = {pkg["version"] for pkg in release["applicationPackages"]}
    check(bool(versions), "published_packages_missing")
    status = client.api(suffix=f"/submissions/{pending}/status").get("status") if pending else None
    check(status is None or re.fullmatch(r"[A-Za-z]{1,40}", status), "unexpected_store_status")
    return {"publishedSubmissionId": published, "publishedVersions": sorted(versions, key=read_version),
            "pendingSubmissionId": pending, "pendingStatus": status}


def updated_package_payload(draft, version, files):
    check(draft.get("status") == "PendingCommit", "store_not_a_draft")
    check((draft.get("pricing") or {}).get("priceId") == "Free", "store_not_free_product")
    packages = draft.get("applicationPackages")
    check(isinstance(packages, list) and len(packages) > 0, "published_packages_missing")
    proposed = read_version(version)
    for pkg in packages:
        check(pkg.get("fileStatus") == "Uploaded" and read_version(pkg.get("version")) < proposed,
              "store_version_not_increasing_or_existing_files_unexpected")
    result = copy.deepcopy(draft)
    result["targetPublishMode"] = "Immediate"
    for pkg in result["applicationPackages"]:
        pkg["fileStatus"] = "PendingDelete"
    result["applicationPackages"].extend({
        "fileName": name, "fileStatus": "PendingUpload",
        "minimumDirectXVersion": "None", "minimumSystemRam": "None",
    } for name in files)
    return result


def matching_packages(release, files):
    present = {p.get("fileName") for p in release.get("applicationPackages", [])
               if p.get("fileStatus") in {"PendingUpload", "Uploaded"}}
    return present == set(files)


def submit(client, version, files):
    published_id, pending = app_state(client)
    if pending:
        existing = client.api(suffix=f"/submissions/{pending}")
        check(existing.get("targetPublishMode") == "Immediate" and matching_packages(existing, files),
              "existing_store_submission_protected")
        status = client.api(suffix=f"/submissions/{pending}/status").get("status")
        check(status in SUBMITTED, "existing_store_draft_or_failure_protected")
        return {"submissionId": pending, "status": status, "reused": True}
    published = client.api(suffix=f"/submissions/{published_id}")
    check(published.get("status") == "Published", "published_submission_not_ready")
    if matching_packages(published, files):
        check(all(pkg.get("version") == version for pkg in published["applicationPackages"]
                  if pkg.get("fileStatus") == "Uploaded"), "published_package_version_mismatch")
        return {"submissionId": published_id, "status": "Published", "reused": True}
    # Prove monotonicity and existing metadata before creating any draft.
    updated_package_payload({**published, "status": "PendingCommit"}, version, files)
    check(app_state(client) == (published_id, None), "store_submission_changed")
    draft = client.api("POST", "/submissions")
    draft_id = submission_id(draft.get("id"))
    print(json.dumps({"createdUncommittedSubmissionId": draft_id}), flush=True)
    payload = updated_package_payload(draft, version, files)
    check(app_state(client) == (published_id, draft_id), "store_draft_ownership_changed")
    client.api("PUT", f"/submissions/{draft_id}", payload)
    client.upload(draft["fileUploadUrl"], files)
    staged = client.api(suffix=f"/submissions/{draft_id}")
    check(staged.get("status") == "PendingCommit" and staged.get("targetPublishMode") == "Immediate"
          and matching_packages(staged, files), "staged_store_packages_not_verified")
    check(app_state(client) == (published_id, draft_id), "store_draft_ownership_changed")
    try:
        result = client.api("POST", f"/submissions/{draft_id}/commit")
    except StoreError:
        # The POST can succeed remotely even if the response is lost. Never resend.
        result = client.api(suffix=f"/submissions/{draft_id}/status")
    status = result.get("status")
    check(status in SUBMITTED, "store_certification_not_confirmed")
    return {"submissionId": draft_id, "status": status, "reused": False}


def main():
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="action", required=True)
    tag = commands.add_parser("verify-tag")
    tag.add_argument("tag")
    tag.add_argument("props", type=Path)
    release = commands.add_parser("submit")
    release.add_argument("commit")
    release.add_argument("props", type=Path)
    release.add_argument("artifacts", type=Path)
    commands.add_parser("inventory")
    args = parser.parse_args()
    try:
        if args.action == "verify-tag":
            verify_tag(args.tag, args.props)
        elif args.action == "inventory":
            print(json.dumps(inventory(StoreClient(os.environ))))
        else:
            _, version = props_versions(args.props)
            files = verify_artifacts(args.commit, version, args.artifacts)
            print(json.dumps(submit(StoreClient(os.environ), version, files)))
    except StoreError as exc:
        raise SystemExit(str(exc)) from None
    except Exception:
        raise SystemExit("unexpected_store_sync_error") from None


if __name__ == "__main__":
    main()
