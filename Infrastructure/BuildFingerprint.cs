using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace PeakSafeOptimizer.Infrastructure
{
    public sealed class BuildFingerprint
    {
        // MVID of the Assembly-CSharp the equivalence arguments were last re-audited against. Reported
        // in the startup log and nothing else: it does not gate installation, so a game update that
        // leaves the members a patch reads unchanged keeps that patch working without a release. When
        // the log shows structural-validation, the Fingerprint and StrictIl patches are the ones worth
        // re-reading
        public static readonly Guid CurrentAssemblyCSharpMvid = new Guid("b5521262-0ce1-4a6c-a2bb-f18e027e413e");

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
