// Copy to: UnityClient/Assets/UOClient/Editor/PatchBuild.cs
//
// SKETCH: the Addressables calls (AddressableAssetSettingsDefaultObject, BuildPlayerContent) are the
// public 1.x/2.x editor API; confirm against the installed package. ReleaseBuilder.Build(outputDir) is the
// existing public entry point in Assets/UOClient/Editor/ReleaseBuilder.cs.
//
// Produces Builds/Patch/UOClient: the exact folder players should end up with
//   UOClient.exe, UOClient_Data/...            (IL2CPP player, from ReleaseBuilder)
//   Content/StandaloneWindows64/catalog_content.(bin|json|hash), *.bundle   (Addressables)
// which Tools/Release/publish-patch.ps1 hands to `patchtool publish`.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UOClient.Content;

namespace UOClient.EditorTools
{
    public static class PatchBuild
    {
        public const string OutputDir = "Builds/Patch/UOClient";
        public const string ProfileName = "Patch";
        public const string TargetFolder = "StandaloneWindows64";   // = [BuildTarget] for Win64
        /// <summary>Addressables groups whose bundles hold paid art and must be encrypted.</summary>
        public const string ProtectedGroupPrefix = "Protected_";

        [MenuItem("UO Client/Release/Build Patch")]
        public static void BuildMenu() => Build(OutputDir, buildPlayer: true);

        [MenuItem("UO Client/Release/Build Patch (content only)")]
        public static void BuildContentOnlyMenu() => Build(OutputDir, buildPlayer: false);

        /// <summary>batch mode: -executeMethod UOClient.EditorTools.PatchBuild.BuildFromCommandLine [-contentOnly]</summary>
        public static void BuildFromCommandLine()
        {
            bool contentOnly = Environment.GetCommandLineArgs().Contains("-contentOnly");
            if (!Build(OutputDir, !contentOnly)) EditorApplication.Exit(1);
        }

        public static bool Build(string outputDir, bool buildPlayer)
        {
            outputDir = Path.GetFullPath(outputDir);
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            string profileId = settings.profileSettings.GetProfileId(ProfileName);
            if (string.IsNullOrEmpty(profileId)) { Debug.LogError($"[Patch] No Addressables profile '{ProfileName}'."); return false; }
            settings.activeProfileId = profileId;

            // 1. Addressables content -> ServerData/StandaloneWindows64 (Remote.BuildPath).
            string serverData = Path.GetFullPath(Path.Combine("ServerData", TargetFolder));
            if (Directory.Exists(serverData)) Directory.Delete(serverData, true);
            AddressableAssetSettings.BuildPlayerContent(out var result);
            if (!string.IsNullOrEmpty(result.Error)) { Debug.LogError("[Patch] Addressables build failed: " + result.Error); return false; }

            // 2. Encrypt the paid-art bundles, deterministically (see EncryptBundles).
            EncryptBundles(serverData, settings);

            // 3. Player. ReleaseBuilder wipes outputDir, so content is copied in afterwards.
            //    Content-only: keep the previous player and only swap Content/ (no code changes allowed!).
            if (buildPlayer)
            {
                if (!ReleaseBuilder.Build(outputDir)) return false;
            }
            else if (!File.Exists(Path.Combine(outputDir, ReleaseBuilder.ExeName)))
            {
                Debug.LogError("[Patch] Content-only build needs an existing player in " + outputDir); return false;
            }

            // 4. Content next to the player, where PatchPaths.ContentRoot points.
            string contentDir = Path.Combine(outputDir, "Content", TargetFolder);
            if (Directory.Exists(contentDir)) Directory.Delete(contentDir, true);
            CopyDir(serverData, contentDir);

            Debug.Log($"[Patch] OK: {outputDir}. Publish with Tools/Release/publish-patch.ps1 -Version <x.y.z>");
            return true;
        }

        /// <summary>
        /// Encrypts each bundle of a Protected_* group in place, with the same header + AES-CTR as content.pak.
        /// The nonce is derived from the bundle's plaintext hash instead of being random, so an unchanged
        /// bundle encrypts to the identical file every build and the updater doesn't re-download it.
        /// (Same key + same nonce only ever happens for identical plaintext, which is safe for CTR.)
        /// </summary>
        private static void EncryptBundles(string serverData, AddressableAssetSettings settings)
        {
            byte[] key = ContentKey.Get(); // same source as ReleaseBuilder uses today
            var protectedGroups = settings.groups.Where(g => g != null && g.Name.StartsWith(ProtectedGroupPrefix)).Select(g => g.Name.ToLowerInvariant()).ToArray();
            foreach (string bundle in Directory.GetFiles(serverData, "*.bundle", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(bundle).ToLowerInvariant();
                // Default bundle naming starts with the group name; adjust if you change "Bundle Naming Mode".
                if (!protectedGroups.Any(g => name.StartsWith(g))) continue;

                byte[] plain = File.ReadAllBytes(bundle);
                byte[] nonce = new byte[CtrCipherStream.NonceSize];
                using (var sha = SHA256.Create()) Buffer.BlockCopy(sha.ComputeHash(plain), 0, nonce, 0, nonce.Length);

                var header = new byte[ProtectedContent.HeaderSize];
                Buffer.BlockCopy(ProtectedContent.Magic, 0, header, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(ProtectedContent.FormatVersion), 0, header, 4, 4);
                Buffer.BlockCopy(nonce, 0, header, 8, nonce.Length);

                using (var ecb = CtrCipherStream.CreateEcb(key))
                {
                    var counters = new byte[plain.Length + 16];
                    var keystream = new byte[plain.Length + 16];
                    CtrCipherStream.Xor(ecb, nonce, 0, plain, 0, plain.Length, counters, keystream);
                }
                using (var fs = new FileStream(bundle, FileMode.Create, FileAccess.Write))
                {
                    fs.Write(header, 0, header.Length);
                    fs.Write(plain, 0, plain.Length);
                }
            }
        }

        private static void CopyDir(string from, string to)
        {
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to));
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to), true);
        }
    }
}
