using System;
using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using BepInEx;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace FreePlayPlus
{
    // Makes saved custom ships (ModdedShipLoader and similar) load as fully working ships, without
    // modifying the mod that adds them.
    //
    // A custom ship's bundle contains TypeAssets (ObjectInfo/StructurePart assets and the like)
    // that its prefabs reference directly but that have no entry in the ship's catalog.json. A fresh
    // spawn never needs them by key. A saved ship (.ship file - FreePlay saves, and career's
    // in-progress ship) records them by AssetGUID and loads them back through Addressables, which
    // fails ("No Location found for Key=..."): the parts come back without a StructurePart, so they
    // can't be cut, show no part info, and saved cuts aren't re-applied.
    //
    // This is an extra Addressables locator. For a key it doesn't know yet, it finds the mod bundles
    // through the loaded catalogs themselves - bundle locations whose file is under BepInEx\plugins
    // (e.g. ...\ModdedShipLoader\Ships\Tachi.KerballOne\Tachi_assets_all.bundle) - and asks the
    // ResourceManager for each one, which hands back the AssetBundle Addressables already loaded.
    // Each bundle's TypeAssets are indexed by AssetGUID once, and served from memory. Bundle
    // internal names are hashes (for the game's bundles too), so matching loaded bundles by name
    // doesn't work. Keys that no locator at all can resolve are logged once (DebugLogging).
    internal sealed class CustomShipAssetLocator : IResourceLocator
    {
        private const string kProviderId = "FreePlayPlus.CustomShipAssetProvider";

        private static CustomShipAssetLocator? sInstance;
        private readonly string mPluginPath = NormalizePath(Paths.PluginPath);
        private readonly HashSet<string> mIndexedBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AsyncOperationHandle<IAssetBundleResource>> mBundleHandles = new List<AsyncOperationHandle<IAssetBundleResource>>();
        private readonly Dictionary<string, UnityEngine.Object> mAssets = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> mLoggedMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string LocatorId => "FreePlayPlus.CustomShipAssetLocator";

        public IEnumerable<object> Keys => mAssets.Keys;

        // Called from Plugin.Update until Addressables has initialised (the game initialises it at boot).
        internal static void TryInstall()
        {
            if (sInstance != null) return;
            if (Addressables.ResourceManager == null || !Addressables.ResourceLocators.GetEnumerator().MoveNext()) return;

            sInstance = new CustomShipAssetLocator();
            int modBundleFiles = 0;
            try { modBundleFiles = Directory.GetFiles(Paths.PluginPath, "*.bundle", SearchOption.AllDirectories).Length; }
            catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't list mod bundles: {ex.Message}"); }
            if (modBundleFiles == 0)
            {
                Plugin.Debug("No custom ship bundles found; saved-ship asset locator not needed.");
                return;
            }
            Addressables.ResourceManager.ResourceProviders.Add(new CustomShipAssetProvider());
            Addressables.AddResourceLocator(sInstance);
            Plugin.Log.LogInfo($"Saved-ship asset locator installed ({modBundleFiles} mod bundle file(s)).");
        }

        internal static bool TryGet(string key, out UnityEngine.Object asset)
        {
            asset = null!;
            return sInstance != null && sInstance.mAssets.TryGetValue(key, out asset) && asset != null;
        }

        public bool Locate(object key, Type type, out IList<IResourceLocation> locations)
        {
            locations = null!;
            var name = key as string ?? (key as IKeyEvaluator)?.RuntimeKey as string;
            if (string.IsNullOrEmpty(name) || !LooksLikeGuid(name!)) return false;

            if (!mAssets.TryGetValue(name!, out var asset) || asset == null)
            {
                if (KnownElsewhere(key, type)) return false; // a real catalog has it; nothing to do
                IndexModBundles();
                if (!mAssets.TryGetValue(name!, out asset) || asset == null)
                {
                    if (Plugin.DebugLogging.Value && mLoggedMisses.Add(name!))
                        Plugin.Log.LogInfo($"[SavedShipAssets] No asset anywhere for key {name} (type {type?.Name ?? "any"}).");
                    return false;
                }
            }
            if (type != null && type != typeof(object) && !type.IsInstanceOfType(asset)) return false;

            locations = new List<IResourceLocation> { new ResourceLocationBase(name, name, kProviderId, asset.GetType()) };
            return true;
        }

        // Addressables asks every locator about every key; only act on keys nobody else resolves.
        private bool KnownElsewhere(object key, Type type)
        {
            foreach (var locator in Addressables.ResourceLocators)
                if (!ReferenceEquals(locator, this) && locator.Locate(key, type, out _)) return true;
            return false;
        }

        private static bool LooksLikeGuid(string key)
        {
            if (key.Length != 32) return false;
            foreach (var c in key)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        private static string NormalizePath(string path) => path.Replace('/', '\\').TrimEnd('\\');

        private void IndexModBundles()
        {
            foreach (var location in ModBundleLocations())
            {
                if (mIndexedBundles.Contains(location.InternalId)) continue;
                AsyncOperationHandle<IAssetBundleResource> handle;
                try { handle = Addressables.ResourceManager.ProvideResource<IAssetBundleResource>(location); }
                catch (Exception ex) { Plugin.Debug($"[SavedShipAssets] Can't open {Path.GetFileName(location.InternalId)}: {ex.Message}"); continue; }

                // Already loaded bundles complete immediately; one that isn't loaded yet is retried on
                // the next miss rather than waited on here (Locate runs inside Addressables calls).
                if (!handle.IsDone || handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
                {
                    if (handle.IsDone) Addressables.ResourceManager.Release(handle);
                    else mBundleHandles.Add(handle);
                    continue;
                }
                mBundleHandles.Add(handle); // keep our reference so the bundle stays loaded
                mIndexedBundles.Add(location.InternalId);

                var bundle = handle.Result.GetAssetBundle();
                var added = 0;
                if (bundle != null)
                {
                    foreach (var asset in bundle.LoadAllAssets())
                    {
                        if (!(asset is TypeAsset typeAsset)) continue;
                        var guid = typeAsset.AssetGUID;
                        if (string.IsNullOrEmpty(guid) || mAssets.ContainsKey(guid)) continue;
                        mAssets[guid] = asset;
                        added++;
                    }
                }
                Plugin.Debug($"[SavedShipAssets] Indexed {added} asset(s) from {Path.GetFileName(location.InternalId)}.");
            }
        }

        // Bundle locations, from every loaded catalog, whose bundle file lives under BepInEx\plugins.
        private IEnumerable<IResourceLocation> ModBundleLocations()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (ReferenceEquals(locator, this) || !(locator is ResourceLocationMap map)) continue;
                foreach (var list in map.Locations.Values)
                {
                    foreach (var location in list)
                    {
                        var id = location.InternalId;
                        if (string.IsNullOrEmpty(id) || !id.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!NormalizePath(id).StartsWith(mPluginPath, StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(id)) yield return location;
                    }
                }
            }
        }
    }

    internal sealed class CustomShipAssetProvider : ResourceProviderBase
    {
        public CustomShipAssetProvider() => m_ProviderId = "FreePlayPlus.CustomShipAssetProvider";

        public override void Provide(ProvideHandle provideHandle)
        {
            var key = provideHandle.Location.InternalId;
            if (CustomShipAssetLocator.TryGet(key, out var asset))
                provideHandle.Complete(asset, true, null);
            else
                provideHandle.Complete<UnityEngine.Object>(null!, false, new Exception($"FreePlayPlus: custom-ship asset {key} is no longer loaded."));
        }
    }
}
