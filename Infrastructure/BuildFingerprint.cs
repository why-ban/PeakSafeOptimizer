using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace PeakSafeOptimizer.Infrastructure
{
    public sealed class BuildFingerprint
    {
        /// <summary>
        /// MVID of the Assembly-CSharp this plugin's equivalence arguments were last re-audited
        /// against. It is reported in the startup log and nothing else: it does not gate installation.
        /// A patch installs when its own Initialize/ResolveTarget assertions pass, so a game update
        /// that leaves the members a patch reads unchanged keeps that patch working without a release.
        /// What this constant says is "somebody has read the decompiled bodies of this build" - when
        /// the log shows structural-validation instead of certified, the Fingerprint and StrictIl
        /// patches are the ones worth re-reading.
        /// </summary>
        public static readonly Guid CurrentAssemblyCSharpMvid = new Guid("c88cc526-8d4e-40fa-afdb-e24e4c233bd0");

        private BuildFingerprint(Guid actualMvid, string sha256)
        {
            ActualMvid = actualMvid;
            Sha256 = sha256;
        }

        public Guid ActualMvid { get; }
        public string Sha256 { get; }
        public bool IsCurrent => ActualMvid == CurrentAssemblyCSharpMvid;

        public static BuildFingerprint Capture()
        {
            Assembly assembly = typeof(CharacterData).Assembly;
            string sha = "unavailable";
            try
            {
                using (FileStream stream = File.OpenRead(assembly.Location))
                using (SHA256 algorithm = SHA256.Create())
                    sha = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
            catch (Exception ex)
            {
                sha = $"unavailable:{ex.GetType().Name}";
            }
            return new BuildFingerprint(assembly.ManifestModule.ModuleVersionId, sha);
        }

        public string ToDiagnosticString() =>
            $"Assembly-CSharp fingerprint: MVID={ActualMvid}, expected={CurrentAssemblyCSharpMvid}, current={IsCurrent}, SHA256={Sha256}";
    }
}
