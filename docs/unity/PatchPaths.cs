// Copy to: UnityClient/Assets/UOClient/Scripts/Patching/PatchPaths.cs
// Referenced by the Addressables profile variable Remote.LoadPath = {UOClient.Patching.PatchPaths.ContentRoot}/[BuildTarget]
using System.IO;
using UnityEngine;

namespace UOClient.Patching
{
    /// <summary>
    /// Where patchable Addressables content lives at runtime. GameUpdater installs it next to the player as
    /// &lt;install&gt;/Content/&lt;BuildTarget&gt;/ (catalog + bundles), and Addressables loads it from there as its
    /// "remote" location. Nothing is fetched over the network by the game itself: the updater already
    /// downloaded (only) the changed bundles and verified them.
    /// </summary>
    public static class PatchPaths
    {
        /// <summary>
        /// Player: &lt;install&gt;/Content (Application.dataPath is &lt;install&gt;/UOClient_Data).
        /// Editor with "Use Existing Build" play mode: &lt;project&gt;/ServerData.
        /// Must be a static field or property: Addressables evaluates "{UOClient.Patching.PatchPaths.ContentRoot}" by reflection.
        /// </summary>
        public static string ContentRoot
        {
            get
            {
#if UNITY_EDITOR
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ServerData")).Replace('\\', '/');
#else
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Content")).Replace('\\', '/');
#endif
            }
        }
    }
}
