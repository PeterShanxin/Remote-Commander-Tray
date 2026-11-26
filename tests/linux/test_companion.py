import importlib.machinery
import importlib.util
import json
from pathlib import Path, PurePosixPath
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch


LAUNCHER = Path(__file__).resolve().parents[2] / "linux/remote-commander-tray"
loader = importlib.machinery.SourceFileLoader("linux_companion", str(LAUNCHER))
spec = importlib.util.spec_from_loader(loader.name, loader)
app = importlib.util.module_from_spec(spec)
loader.exec_module(app)


class LinuxCompanionTests(unittest.TestCase):
    @patch.object(app, "control")
    def test_status_does_not_claim_remote_connectivity(self, control):
        control.return_value = subprocess.CompletedProcess([], 0, "LoadState=loaded\nActiveState=active\nUnitFileState=enabled\n", "")
        self.assertEqual(app.status(), "Running (connection not verified) | Start with user session: on")

    @patch.object(app, "control")
    def test_failed_service_limit_clears_only_on_explicit_start(self, control):
        control.side_effect = [
            subprocess.CompletedProcess([], 0, "failed\n", ""),
            subprocess.CompletedProcess([], 0),
            subprocess.CompletedProcess([], 0),
        ]
        app.change("start")
        self.assertEqual([args[:1] for args, kwargs in control.call_args_list],
                         [("is-failed",), ("reset-failed",), ("start",)])
        control.reset_mock(side_effect=True)
        control.return_value = subprocess.CompletedProcess([], 0)
        app.change("stop")
        control.assert_called_once_with("stop", app.UNIT)

    @patch.object(app.os, "execve")
    def test_official_cli_is_run_directly_and_path_has_installed_node(self, execve):
        command = PurePosixPath("/tmp/安装目录 with space/bin/desktop-commander")
        with tempfile.TemporaryDirectory() as directory:
            settings = Path(directory) / "agent.json"
            settings.write_text(json.dumps({"executable": str(command), "nodeDirectory": "/tmp/Node bin"}))
            with patch.object(app, "COMMAND", settings), patch.object(app, "Path", PurePosixPath), \
                 patch.object(app.os, "environ", {"PATH": "/changed/shell/path"}):
                app.run_agent()
        target, argv, environment = execve.call_args.args
        self.assertEqual(target, str(command))
        self.assertEqual(argv, [str(command), "remote"])
        self.assertEqual(environment["PATH"].split(app.os.pathsep)[:2], ["/tmp/Node bin", str(command.parent)])

    def test_stop_during_status_refresh_is_applied_once(self):
        callbacks = {}
        commands = {}
        workers = []
        label = Mock()
        window = Mock()
        window.after.side_effect = lambda delay, callback: callbacks.__setitem__(delay, callback)

        def button(parent, *, text, command):
            commands[text] = command
            return Mock()

        def exercise_window():
            callbacks.pop(0)()
            self.assertEqual(len(workers), 1)
            commands["Stop"]()
            commands["Stop"]()
            workers.pop(0)()
            callbacks[100]()
            self.assertEqual(len(workers), 1, "Stop should run when the status refresh completes")
            workers.pop(0)()
            callbacks[100]()

        window.mainloop.side_effect = exercise_window
        tkinter = SimpleNamespace(Tk=lambda: window, StringVar=lambda **kwargs: label,
                                  ttk=SimpleNamespace(Frame=Mock(), Label=Mock(), Button=button),
                                  messagebox=Mock())
        with patch.dict(sys.modules, {"tkinter": tkinter}), \
             patch("threading.Thread", side_effect=lambda target, daemon: Mock(start=lambda: workers.append(target))), \
             patch.object(app, "change") as change, patch.object(app, "status", return_value="Stopped"):
            app.gui()
            change.assert_called_once_with("stop")
        label.set.assert_called_with("Stopped")

    def test_missing_manager_does_not_change_user_data(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory) / "never-created"
            fail = Mock(return_value=subprocess.CompletedProcess([], 1))
            with patch.object(app, "DATA", data), patch.object(app, "control", fail):
                with self.assertRaisesRegex(ValueError, "running systemd user session"):
                    app.install()
            self.assertFalse(data.exists())

    def test_missing_node_or_cli_does_not_create_user_files(self):
        with tempfile.TemporaryDirectory() as directory:
            data = Path(directory) / "never-created"
            for dependency in ("node", "desktop-commander"):
                with self.subTest(dependency=dependency), patch.object(app, "DATA", data), \
                     patch.object(app, "control", return_value=subprocess.CompletedProcess([], 0)), \
                     patch.object(app.shutil, "which", side_effect=lambda name: None if name == dependency else name):
                    with self.assertRaisesRegex(ValueError, "Install Node.js and the official Desktop Commander CLI"):
                        app.install()
                    self.assertFalse(data.exists())
