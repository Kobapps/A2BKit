using A2BKit.Core;
using System;
using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace A2BKit.Editor
{
    /// <summary>
    /// Installs A2BKit's AI skill into the project so an assistant working in this repo knows the
    /// real API instead of guessing at it.
    ///
    /// The skill markdown ships inside the package (under <c>Documentation~/Skill/</c>), so it is
    /// always version-matched to the API it describes. "Install" copies it to the project's
    /// <c>.claude/skills/a2bkit/</c> — the folder Claude Code reads skills from.
    ///
    /// Everything here is path work with no Unity asset involvement, because both ends live outside
    /// the AssetDatabase: the source is a tilde folder (excluded from import) and the destination is a
    /// dotfolder at the project root.
    /// </summary>
    public static class A2BSkillInstaller
    {
        private const string SkillName = "a2bkit";

        /// <summary>What the caller learns about the current install state.</summary>
        public enum State
        {
            /// <summary>Not installed in this project.</summary>
            NotInstalled,

            /// <summary>Installed and byte-identical to the version shipping with the package.</summary>
            UpToDate,

            /// <summary>Installed, but the package now ships a different version.</summary>
            UpdateAvailable,

            /// <summary>The skill file could not be found in the package — a packaging error.</summary>
            SourceMissing
        }

        /// <summary>The project's skills directory, e.g. &lt;project&gt;/.claude/skills/a2bkit.</summary>
        public static string TargetDir
        {
            get
            {
                // Application.dataPath is <project>/Assets; the project root is its parent.
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(projectRoot, ".claude", "skills", SkillName);
            }
        }

        public static string TargetSkillFile => Path.Combine(TargetDir, "SKILL.md");

        /// <summary>
        /// The shipped SKILL.md inside the package.
        ///
        /// Resolved through PackageInfo rather than a hard-coded <c>Packages/…</c> string, because the
        /// package could be embedded, a local path, or a git cache under
        /// <c>Library/PackageCache/…@hash</c> — only PackageInfo knows which. Returns null if the file
        /// is absent (which would be a packaging bug, so it is surfaced, not swallowed).
        /// </summary>
        public static string SourceSkillFile()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(A2BSkillInstaller).Assembly);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
                return null;

            string path = Path.Combine(package.resolvedPath, "Documentation~", "Skill", "SKILL.md");
            return File.Exists(path) ? path : null;
        }

        /// <summary>The package version, for display next to the install state.</summary>
        public static string PackageVersion()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(A2BSkillInstaller).Assembly);
            return package != null ? package.version : "?";
        }

        /// <summary>
        /// Current state. UpToDate vs UpdateAvailable is a straight byte comparison — the shipped file
        /// is the source of truth, so if the two differ the installed one is stale, full stop. No
        /// version stamping is needed, which also means editing the skill by hand shows as
        /// UpdateAvailable, which is correct.
        /// </summary>
        public static State GetState()
        {
            string source = SourceSkillFile();
            if (source == null) return State.SourceMissing;
            if (!File.Exists(TargetSkillFile)) return State.NotInstalled;

            try
            {
                return FilesEqual(source, TargetSkillFile) ? State.UpToDate : State.UpdateAvailable;
            }
            catch (Exception e)
            {
                A2BKit.Core.A2BLog.Exception(null, e);
                return State.NotInstalled;
            }
        }

        /// <summary>
        /// Copies the skill into the project. Overwrites an existing install (that IS the update path).
        /// Returns true on success; logs and returns false on any failure rather than throwing, so a
        /// button handler can't blow up.
        /// </summary>
        public static bool Install()
        {
            string source = SourceSkillFile();
            if (source == null)
            {
                A2BKit.Core.A2BLog.Error(null,
                    "Could not install the A2BKit skill: SKILL.md was not found in the package " +
                    "(expected Documentation~/Skill/SKILL.md). This is a packaging error.");
                return false;
            }

            try
            {
                Directory.CreateDirectory(TargetDir);

                // Copy the whole Skill folder, not just SKILL.md, so any companion reference files
                // ship too — a skill is allowed to be more than one file.
                string sourceDir = Path.GetDirectoryName(source);
                foreach (string file in Directory.GetFiles(sourceDir))
                {
                    string name = Path.GetFileName(file);
                    File.Copy(file, Path.Combine(TargetDir, name), overwrite: true);
                }

                A2BKit.Core.A2BLog.Info(null, "A2BKit skill installed to " + TargetDir);
                return true;
            }
            catch (Exception e)
            {
                A2BKit.Core.A2BLog.Exception(null, e);
                return false;
            }
        }

        /// <summary>Opens the installed (or shipped) skill in the OS's default editor.</summary>
        public static void RevealInstalled()
        {
            string path = File.Exists(TargetSkillFile) ? TargetSkillFile : SourceSkillFile();
            if (!string.IsNullOrEmpty(path)) EditorUtility.RevealInFinder(path);
        }

        private static bool FilesEqual(string a, string b)
        {
            byte[] ba = File.ReadAllBytes(a);
            byte[] bb = File.ReadAllBytes(b);
            if (ba.Length != bb.Length) return false;
            for (int i = 0; i < ba.Length; i++)
                if (ba[i] != bb[i]) return false;
            return true;
        }
    }
}
