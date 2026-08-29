using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AssetUsageDetectorNamespace;
using CupkekGames.AssetVaults;
using CupkekGames.AssetVaults.Editor;
using UnityEditor;
using UnityEngine;

namespace CupkekGames.AssetVaults.UsageReports
{
  /// <summary>
  /// Answers the vault's usage question with yasirkula's Asset Usage Detector.
  ///
  /// <para>The detector is asked the inverse of its usual question. Normally you
  /// give it one object and it finds the references; here it is given every
  /// asset in a folder at once with <c>calculateUnusedObjects</c>, and what
  /// comes back is the set nothing points at. Subtracting that from the input
  /// gives the used list, which is the only thing the vault needs.</para>
  ///
  /// <para>This whole package is that adaptation. It is the same shape as the
  /// Drive backend: the vault knows only its own interface, the detector knows
  /// nothing about vaults, and this one file joins them - so either can be
  /// installed without the other.</para>
  /// </summary>
  public class AssetUsageDetectorReporter : IVaultUsageReporter
  {
    public string DisplayName => "Asset Usage Detector";

    /// <summary>Beats the built-in scan: it walks serialized properties rather
    /// than text, so it sees references no amount of grepping would.</summary>
    public int Priority => 100;

    /// <summary>The detector builds its own cache per Run, so there is nothing to drop.</summary>
    public void Invalidate() { }

    public UsageReport Report(string folder, CancellationToken ct)
    {
      if (!Directory.Exists(folder))
      {
        throw new VaultException($"There is no folder at {folder}");
      }

      string root = Directory.GetParent(Application.dataPath).FullName;
      string relativeFolder = ToProjectRelative(root, folder);

      // Load by GUID rather than by walking the directory: an asset is what the
      // AssetDatabase says it is, and folders hold plenty of files that are not
      // assets at all.
      string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { relativeFolder });
      var assets = new List<UnityEngine.Object>(guids.Length);
      var pathOf = new Dictionary<UnityEngine.Object, string>(guids.Length);
      foreach (string guid in guids)
      {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
        {
          continue;
        }

        UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
        if (asset == null) continue;

        assets.Add(asset);
        pathOf[asset] = path;
      }

      if (assets.Count == 0)
      {
        return new UsageReport(Array.Empty<string>(), 0, DisplayName);
      }

      ct.ThrowIfCancellationRequested();

      var parameters = new AssetUsageDetector.Parameters
      {
        objectsToSearch = assets.ToArray(),
        searchInScenes = SceneSearchMode.AllScenes,
        searchInAssetsFolder = true,
        searchInProjectSettings = true,

        // Without this the folder's own internal wiring counts as usage and
        // every pack looks fully used - a prefab referencing its own material
        // says nothing about whether the GAME needs either.
        dontSearchInSourceAssets = true,

        // The whole reason this tool is here.
        calculateUnusedObjects = true,
        lazySceneSearch = false,
        showDetailedProgressBar = true,
        noAssetDatabaseChanges = false,
      };

      SearchResult result = new AssetUsageDetector().Run(parameters);
      if (result == null)
      {
        throw new OperationCanceledException();
      }

      var unused = new HashSet<UnityEngine.Object>();
      for (int i = 0; i < result.NumberOfGroups; i++)
      {
        SearchResultGroup group = result[i];
        if (group.Type != SearchResultGroup.GroupType.UnusedObjects) continue;

        for (int r = 0; r < group.NumberOfReferences; r++)
        {
          UnityEngine.Object node = group[r].UnityObject;
          if (node != null) unused.Add(node);
        }
      }

      var used = new List<string>();
      foreach (UnityEngine.Object asset in assets)
      {
        if (!unused.Contains(asset) && pathOf.TryGetValue(asset, out string path))
        {
          used.Add(path);
        }
      }

      used.Sort(StringComparer.OrdinalIgnoreCase);
      return new UsageReport(used, assets.Count, DisplayName);
    }

    private static string ToProjectRelative(string projectRoot, string folder)
    {
      string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
      string root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
      if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
      {
        throw new VaultException(
          "The detector searches the AssetDatabase, so it can only report on folders "
          + "inside this project. A folder already moved to the vault is outside it - "
          + "check it before the move, not after.");
      }

      return full.Substring(root.Length + 1).Replace('\\', '/');
    }
  }

  [InitializeOnLoad]
  internal static class AssetUsageDetectorReporterRegistration
  {
    static AssetUsageDetectorReporterRegistration()
    {
      // Prove the engine can start before claiming to be the best reporter
      // installed. The detector resolves Unity internals in its static
      // constructor, and those move: on Unity 6000.6.0b8
      // SpriteAtlasExtensions.GetPackedSprites no longer exists (it is present
      // in 6000.5.3f1), so the whole type fails to initialise. Registering
      // anyway would put a reporter with Priority 100 in front of one that
      // works, and every search would throw.
      try
      {
        // Constructing it runs the class constructor; nothing else is needed.
        AssetUsageDetector probe = new AssetUsageDetector();
        if (probe == null) return;
      }
      catch (TypeInitializationException e)
      {
        Debug.LogWarning(
          "[AssetVault] Asset Usage Detector cannot start on this Unity version, so the "
          + "vault will fall back to its built-in scan. This is upstream's reflection into "
          + "a moved editor internal, not a fault in your project. "
          + e.InnerException?.Message);
        return;
      }
      catch (Exception e)
      {
        Debug.LogWarning("[AssetVault] Asset Usage Detector did not start: " + e.Message);
        return;
      }

      VaultUsageReporters.Register(new AssetUsageDetectorReporter());
    }
  }
}
