// Copy to: UnityClient/Assets/UOClient/Scripts/Content/EncryptedAssetBundleProvider.cs
//
// SKETCH: written against the Addressables 1.x/2.x provider API (ResourceProviderBase, ProvideHandle,
// IAssetBundleResource). Check it compiles against the Addressables version you install for 6000.6 and
// adjust names if the package changed. Before writing your own, look in the installed package for an
// existing stream-processing hook (search for "IDataConverter" or "DataStreamProcessor"); if one exists,
// implement that with CtrCipherStream instead of this provider.
//
// Replaces the single content.pak: each protected Addressables bundle is encrypted on its own (see
// PatchBuild.EncryptBundles), so a change to one pack only changes that pack's bundle file and the
// updater downloads just that file.
using System;
using System.IO;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace UOClient.Content
{
    /// <summary>Set as "Asset Bundle Provider" on every Addressables group that holds paid art.</summary>
    [System.ComponentModel.DisplayName("Encrypted AssetBundle Provider")]
    public sealed class EncryptedAssetBundleProvider : ResourceProviderBase
    {
        public override void Provide(ProvideHandle handle)
        {
            string path = handle.ResourceManager.TransformInternalId(handle.Location);
            try
            {
                var resource = new EncryptedBundleResource(path);
                AssetBundleCreateRequest req = resource.BeginLoad();
                req.completed += _ =>
                {
                    if (req.assetBundle == null)
                    {
                        resource.Unload();
                        handle.Complete<EncryptedBundleResource>(null, false, new Exception($"Failed to load encrypted bundle {path}"));
                    }
                    else handle.Complete(resource, true, null);
                };
            }
            catch (Exception ex)
            {
                handle.Complete<EncryptedBundleResource>(null, false, ex);
            }
        }

        public override Type GetDefaultType(IResourceLocation location) => typeof(IAssetBundleResource);

        public override void Release(IResourceLocation location, object asset)
        {
            (asset as EncryptedBundleResource)?.Unload();
        }

        private sealed class EncryptedBundleResource : IAssetBundleResource
        {
            private readonly string _path;
            private FileStream _file;
            private CtrCipherStream _stream; // must stay open while the bundle is loaded (LoadFromStream requirement)
            private AssetBundleCreateRequest _request;

            public EncryptedBundleResource(string path) { _path = path; }

            public AssetBundleCreateRequest BeginLoad()
            {
                _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
                var header = new byte[ProtectedContent.HeaderSize];
                if (_file.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("Truncated bundle " + _path);
                // Same header as content.pak today: magic(4) | format(4) | nonce(8).
                for (int i = 0; i < 4; i++)
                    if (header[i] != ProtectedContent.Magic[i]) throw new InvalidDataException("Not an encrypted bundle: " + _path);
                var nonce = new byte[CtrCipherStream.NonceSize];
                Buffer.BlockCopy(header, 8, nonce, 0, nonce.Length);
                _stream = new CtrCipherStream(_file, header.Length, _file.Length - header.Length, ContentKey.Get(), nonce);
                _request = AssetBundle.LoadFromStreamAsync(_stream);
                return _request;
            }

            public AssetBundle GetAssetBundle() => _request?.assetBundle;

            public void Unload()
            {
                if (_request != null && _request.assetBundle != null) _request.assetBundle.Unload(true);
                _stream?.Dispose();
                _file?.Dispose();
                _request = null; _stream = null; _file = null;
            }
        }
    }
}
