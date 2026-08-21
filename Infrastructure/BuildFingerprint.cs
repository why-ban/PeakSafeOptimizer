using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace PeakSafeOptimizer.Infrastructure
{
    public sealed class BuildFingerprint
    {
        public static readonly Guid CurrentAssemblyCSharpMvid = new Guid("3f24c13b-87b6-4c9f-9c21-3c49b6ad17e1");

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
