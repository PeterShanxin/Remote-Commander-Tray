import copy
import hashlib
import importlib.util
import json
import struct
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("sync_store", ROOT / "tools/sync-store-release.py")
store = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(store)
VERSION = "1.2.2.0"
SHA = "a" * 40
PKGS = {
    f"RemoteCommanderTray_{VERSION}_{arch}_{SHA}.msix": arch.encode()
    for arch in store.ARCHITECTURES
}


class FakeStore:
    def __init__(self, pending=None, old_version="1.2.1.0"):
        self.pending = pending
        self.calls = []
        self.uploaded = False
        self.lose_commit_response = False
        self.old = {
            "id": "101", "status": "Published", "pricing": {"priceId": "Free"},
            "listings": {"en-us": {"baseListing": {"description": "existing", "images": [{"a": "b"}]}}},
            "applicationPackages": [
                {"version": old_version, "fileStatus": "Uploaded", "fileName": "old.msix"},
            ],
        }
        self.candidate = None

    def api(self, method="GET", suffix="", payload=None):
        self.calls.append((method, suffix))
        if suffix == "":
            return {
                "id": store.PRODUCT_ID, "packageIdentityName": store.IDENTITY_NAME,
                "publisherName": store.PUBLISHER, "lastPublishedApplicationSubmission": {"id": "101"},
                "pendingApplicationSubmission": {"id": "102"} if self.pending else None,
            }
        if method == "POST" and suffix == "/submissions":
            assert not self.pending
            self.pending = "PendingCommit"
            self.candidate = {**self.old, "id": "102", "status": self.pending, "fileUploadUrl": "test"}
            return copy.deepcopy(self.candidate)
        if method == "PUT" and suffix == "/submissions/102":
            self.candidate = copy.deepcopy(payload)
            return {}
        if method == "POST" and suffix == "/submissions/102/commit":
            assert self.uploaded
            self.pending = "Certification"
            if self.lose_commit_response:
                raise store.StoreError("store_request_failed")
            return {"status": self.pending}
        if suffix == "/submissions/101":
            return copy.deepcopy(self.old)
        if suffix == "/submissions/102":
            return copy.deepcopy(self.candidate or {})
        if suffix == "/submissions/102/status":
            return {"status": self.pending}
        raise AssertionError((method, suffix))

    def upload(self, url, files):
        assert url == "test" and files == PKGS
        self.uploaded = True


class SyncTests(unittest.TestCase):
    def test_tag_must_match_source_version(self):
        props = ROOT / "Directory.Build.props"
        current = store.props_versions(props)[0]
        store.verify_tag(f"v{current}", props)
        for tag in ["v9999.9999.9999", f"v{current}-rc", f"v0{current}"]:
            with self.assertRaises(store.StoreError):
                store.verify_tag(tag, props)

    def test_rejects_wrong_receipt_digest_or_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for arch, machine in store.ARCHITECTURES.items():
                folder = root / f"Store-{arch}"
                folder.mkdir()
                name = f"RemoteCommanderTray_{VERSION}_{arch}.msix"
                path = folder / name
                with zipfile.ZipFile(path, "w") as out:
                    out.writestr("AppxManifest.xml", (
                        f'<Package><Identity Name="{store.IDENTITY_NAME}" Publisher="{store.PUBLISHER}" '
                        f'Version="{VERSION}" ProcessorArchitecture="{arch}"/></Package>'
                    ))
                    out.writestr("RemoteCommanderTray.exe", b"MZ" + bytes(58) +
                                 struct.pack("<I4sH", 64, b"PE\0\0", machine))
                (folder / "build-info.json").write_text(json.dumps({
                    "SHA256": hashlib.sha256(path.read_bytes()).hexdigest(), "Package": name,
                    "Architecture": arch, "PackageVersion": VERSION, "StoreId": store.PRODUCT_ID,
                    "SourceCommit": SHA, "WorkingTreeModified": False, "Signed": False,
                    "RequiresStoreCertification": True,
                }))
            self.assertEqual(set(store.verify_artifacts(SHA, VERSION, root)), set(PKGS))
            with self.assertRaisesRegex(store.StoreError, "store_receipt_mismatch"):
                store.verify_artifacts("b" * 40, VERSION, root)
            path.write_bytes(path.read_bytes() + b"changed")
            with self.assertRaisesRegex(store.StoreError, "store_receipt_mismatch"):
                store.verify_artifacts(SHA, VERSION, root)

            # With a matching receipt the independently parsed MSIX still
            # must identify itself as this product and architecture.
            with zipfile.ZipFile(path, "w") as out:
                out.writestr("AppxManifest.xml", '<Package><Identity Name="Wrong.Product"/></Package>')
                out.writestr("RemoteCommanderTray.exe", b"fixture")
            receipt_path = path.parent / "build-info.json"
            wrong_identity_receipt = json.loads(receipt_path.read_text())
            wrong_identity_receipt["SHA256"] = hashlib.sha256(path.read_bytes()).hexdigest()
            receipt_path.write_text(json.dumps(wrong_identity_receipt))
            with self.assertRaisesRegex(store.StoreError, "msix_identity_mismatch"):
                store.verify_artifacts(SHA, VERSION, root)

    def test_submission_preserves_listing_and_deduplicates(self):
        client = FakeStore()
        client.lose_commit_response = True
        result = store.submit(client, VERSION, PKGS)
        self.assertEqual(result, {"submissionId": "102", "status": "Certification", "reused": False})
        self.assertEqual(client.candidate["listings"], client.old["listings"])
        self.assertEqual(client.candidate["pricing"], client.old["pricing"])
        self.assertEqual(client.candidate["applicationPackages"][0]["fileStatus"], "PendingDelete")
        self.assertTrue(store.submit(client, VERSION, PKGS)["reused"])
        self.assertEqual(client.calls.count(("POST", "/submissions/102/commit")), 1)
        self.assertEqual(client.calls.count(("POST", "/submissions")), 1)

    def test_never_overwrites_existing_or_nonincreasing_release(self):
        for client in [FakeStore(pending="PendingCommit"), FakeStore(old_version=VERSION)]:
            with self.assertRaises(store.StoreError):
                store.submit(client, VERSION, PKGS)
            self.assertTrue(all(method == "GET" for method, _ in client.calls))

    def test_matching_uncommitted_draft_is_never_retried(self):
        client = FakeStore(pending="PendingCommit")
        client.candidate = {
            "targetPublishMode": "Immediate",
            "applicationPackages": [{"fileName": name, "fileStatus": "PendingUpload"} for name in PKGS],
        }
        with self.assertRaisesRegex(store.StoreError, "existing_store_draft_or_failure_protected"):
            store.submit(client, VERSION, PKGS)
        self.assertTrue(all(method == "GET" for method, _ in client.calls))

    def test_inventory_never_mutates(self):
        client = FakeStore()
        self.assertEqual(store.inventory(client)["publishedVersions"], ["1.2.1.0"])
        self.assertTrue(all(method == "GET" for method, _ in client.calls))


if __name__ == "__main__":
    unittest.main()
