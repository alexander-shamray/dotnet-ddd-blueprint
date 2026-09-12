#!/usr/bin/env python3
"""What the gate is looking at, and what it does when it finds nothing.

The gate's own argument is that a build wrote nothing into `src/`. The failure
that argument cannot survive is a walk that reads no tree: every assertion the
gate makes is satisfied by an empty set, and an empty set is exactly what a
renamed source root, a moved solution file or a typo in `SOURCE_ROOTS`
produces. So most of what follows is about the subject rather than the verdict
— a green result has to be a claim about 33 projects and not about none.

The last class of test reads the real repository instead of a fixture, because
the subject is the one thing a fixture cannot check: a tree this gate does not
walk is invisible to every test that builds its own tree.

    py -3.12 -m unittest discover -s .github/output-gate
"""
from __future__ import annotations

import io
import contextlib
import tempfile
import unittest
from pathlib import Path

import output_gate


def tree(root: Path, projects: dict[str, str], built: list[str] | None = None,
         listed: list[str] | None = None) -> None:
    """A repository in miniature: some projects, a solution, some output.

    `projects` maps a project name to the directory it lives in, relative to
    the root. `built` names the projects that have an `artifacts/obj/` entry
    and defaults to all of them; `listed` names the ones the solution carries
    and defaults to the same. The two defaults are what makes a test that
    overrides one of them a test about exactly that difference.
    """
    built = list(projects) if built is None else built
    listed = list(projects) if listed is None else listed

    for name, directory in projects.items():
        project_directory = root / directory
        project_directory.mkdir(parents=True, exist_ok=True)
        (project_directory / f"{name}.csproj").write_text("<Project />", encoding="utf-8")

    for name in built:
        (root / "artifacts" / "obj" / name).mkdir(parents=True, exist_ok=True)

    entries = "".join(
        f'  <Project Path="{projects[name]}/{name}.csproj" />\n' for name in listed)
    (root / "Platform.slnx").write_text(
        f"<Solution>\n{entries}</Solution>\n", encoding="utf-8")


def run(root: Path) -> tuple[int, str]:
    """The gate's exit code and everything it printed."""
    captured = io.StringIO()
    with contextlib.redirect_stdout(captured):
        code = output_gate.main(["--repo", str(root)])
    return code, captured.getvalue()


class TemporaryRepository(unittest.TestCase):
    """Each test gets its own tree; nothing here touches the checkout."""

    def setUp(self) -> None:
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / "src").mkdir()
        (self.root / "tests").mkdir()


class CleanTree(TemporaryRepository):

    def test_a_built_tree_with_no_output_beside_the_source_passes(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"})

        code, output = run(self.root)

        self.assertEqual(code, 0)
        # The count is in the success line because a gate that says only "OK"
        # says the same thing whether it looked at 33 projects or at none.
        self.assertIn("2 project(s)", output)

    def test_a_directory_merely_named_like_output_elsewhere_is_not_a_finding(self) -> None:
        """`bin` and `obj` are matched as directory names, not as substrings."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src" / "Services" / "Catalog" / "Catalog.Domain" / "Binding").mkdir()
        (self.root / "src" / "Services" / "Catalog" / "Catalog.Domain" / "Objects").mkdir()

        code, _output = run(self.root)

        self.assertEqual(code, 0)


class Residue(TemporaryRepository):

    def test_an_obj_beside_a_project_fails(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("src/Services/Catalog/Catalog.Domain/obj/", output)

    def test_a_bin_under_tests_fails(self) -> None:
        """Both roots are walked. `tests/` is source too (Section 4.1)."""
        tree(self.root, {"Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"})
        (self.root / "tests/Catalog.Domain.Tests/bin").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("tests/Catalog.Domain.Tests/bin/", output)

    def test_output_is_caught_at_any_depth_not_only_beside_a_csproj(self) -> None:
        """The claim is about the tree, not about project directories.

        A `bin/` in a directory that holds no `.csproj` is still a build's
        leavings in a source tree, and a gate keyed on project directories
        would walk straight past it.
        """
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/Products/obj").mkdir(parents=True)

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("Products/obj/", output)

    def test_case_does_not_let_output_through(self) -> None:
        """`.gitignore` writes `[Oo]bj/`; this gate agrees with it."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/Obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("Obj/", output)

    def test_a_nested_layout_is_reported_once_at_its_outermost_directory(self) -> None:
        """One finding, and it names the directory to delete."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "src/Services/Catalog/Catalog.Domain/obj/Debug/net10.0").mkdir(parents=True)
        (self.root / "src/Services/Catalog/Catalog.Domain/obj/Debug/bin").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)


class Subject(TemporaryRepository):
    """The half that stops a green result from being about nothing."""

    def test_an_empty_walk_is_a_failure_rather_than_a_pass(self) -> None:
        """The one this suite exists for.

        No projects on disk and none in the solution satisfies every other
        assertion the gate makes. Reported as a finding, it is a gate saying it
        has lost its subject; reported as success, it is the silent stop this
        repository keeps paying for.
        """
        tree(self.root, {})

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("no projects found at all", output)

    def test_a_project_on_disk_and_not_in_the_solution_fails(self) -> None:
        tree(self.root,
             {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
              "Payments.Domain": "src/Services/Payments/Payments.Domain"},
             listed=["Catalog.Domain"])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("absent from Platform.slnx", output)
        self.assertIn("Payments.Domain", output)

    def test_a_project_in_the_solution_and_not_on_disk_fails(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src/Services/Catalog/Catalog.Domain/'
            'Catalog.Domain.csproj" />\n  <Project Path="src/Gateway/Gateway.Api/'
            'Gateway.Api.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("which is not on disk", output)

    def test_a_subject_mismatch_suppresses_the_other_findings(self) -> None:
        """Three reports of one defect is a worse report than one.

        With the walk and the solution disagreeing, neither the missing-output
        check nor the residue check is about the repository the solution
        describes, so their findings would be noise around the one that matters.
        """
        tree(self.root,
             {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
              "Payments.Domain": "src/Services/Payments/Payments.Domain"},
             built=[], listed=["Catalog.Domain"])
        (self.root / "src/Services/Catalog/Catalog.Domain/obj").mkdir()

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)
        self.assertNotIn("artifacts/obj/", output)

    def test_a_solution_written_with_backslashes_is_read(self) -> None:
        """A `.slnx` that has been through a Windows tool spells paths its way."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain"})
        (self.root / "Platform.slnx").write_text(
            '<Solution>\n  <Project Path="src\\Services\\Catalog\\Catalog.Domain\\'
            'Catalog.Domain.csproj" />\n</Solution>\n', encoding="utf-8")

        code, output = run(self.root)

        self.assertEqual(code, 0, output)


class BuildRan(TemporaryRepository):
    """The positive half: the output is somewhere, and it is there."""

    def test_a_tree_nobody_built_fails_rather_than_passing_cleanly(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"},
             built=[])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("no build has run here", output)

    def test_no_build_is_one_finding_and_not_one_per_project(self) -> None:
        """A different defect from a project the build skipped, worded as one."""
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests",
                         "Gateway.Api": "src/Gateway/Gateway.Api"},
             built=[])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("1 finding(s)", output)

    def test_a_single_project_the_build_skipped_is_named(self) -> None:
        tree(self.root, {"Catalog.Domain": "src/Services/Catalog/Catalog.Domain",
                         "Catalog.Domain.Tests": "tests/Catalog.Domain.Tests"},
             built=["Catalog.Domain"])

        code, output = run(self.root)

        self.assertEqual(code, 1)
        self.assertIn("artifacts/obj/Catalog.Domain.Tests/", output)


class ThisRepository(unittest.TestCase):
    """Read against the checkout, because a fixture cannot check the subject.

    Every test above builds the tree it then walks, so all of them would still
    pass on the day `SOURCE_ROOTS` stopped naming where this repository keeps
    its code. These are the assertions that notice.
    """

    def test_every_project_in_the_solution_lives_under_a_walked_root(self) -> None:
        listed = output_gate.solution_projects(output_gate.REPO_ROOT / "Platform.slnx")
        self.assertTrue(listed, "Platform.slnx lists no projects")

        for name, path in sorted(listed.items()):
            self.assertIn(
                path.parts[0], output_gate.SOURCE_ROOTS,
                f"{name} is under {path.parts[0]}/, which output_gate.SOURCE_ROOTS does "
                f"not name - the gate walks past it and reports on the rest")

    def test_the_walk_and_the_solution_agree_about_this_checkout(self) -> None:
        """The gate's own subject check, run against the repository itself."""
        walked = output_gate.walked_projects(output_gate.REPO_ROOT)
        listed = output_gate.solution_projects(output_gate.REPO_ROOT / "Platform.slnx")

        self.assertEqual(sorted(walked), sorted(listed))

    def test_the_gitignore_still_ignores_what_this_gate_refuses(self) -> None:
        """The gate reports residue; `.gitignore` is why it is never committed.

        The two have to keep agreeing. If `.gitignore` stopped ignoring `obj/`,
        the residue this gate fails on would arrive in a commit first, and the
        gate would be reporting a defect a review had already merged.
        """
        ignored = (output_gate.REPO_ROOT / ".gitignore").read_text(encoding="utf-8")

        self.assertIn("[Bb]in/", ignored)
        self.assertIn("[Oo]bj/", ignored)


if __name__ == "__main__":
    unittest.main()
