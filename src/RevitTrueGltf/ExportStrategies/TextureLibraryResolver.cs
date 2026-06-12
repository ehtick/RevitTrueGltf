using Microsoft.Win32;
using RevitTrueGltf.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RevitTrueGltf.ExportStrategies.Schemas
{
    public enum MaterialLibType { Unknown = 0, Generic = 1, Prism = 2 }
    public enum MaterialLibResolution { Unknown = 0, Low = 1, Medium = 2, High = 3 }

    public class MaterialLib
    {
        public MaterialLibType Type;
        public IList<KeyValuePair<MaterialLibResolution, string>> LibPaths
            = new List<KeyValuePair<MaterialLibResolution, string>>();
    }

    /// <summary>
    /// Responsible for discovering and resolving absolute paths to Autodesk texture files
    /// based on the Windows registry.
    /// </summary>
    public class TextureLibraryResolver
    {
        private static readonly TextureLibraryResolver _instance = new TextureLibraryResolver();
        public static TextureLibraryResolver Instance => _instance;

        private IList<MaterialLib> _materialLibs;
        private readonly Dictionary<string, string> _textureSearchCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private TextureLibraryResolver()
        {
            // Initialization is deferred to EnsureInitialized() because it requires RevitContext.VersionNumber
        }

        public void EnsureInitialized()
        {
            if (_materialLibs == null)
            {
                _materialLibs = BuildMaterialLibs();
            }
        }

        private IList<MaterialLib> BuildMaterialLibs()
        {
            var libs = new List<MaterialLib>();

            // HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Autodesk\ADSKTextureLibraryNew
            var lib = ParseLibRegistry("SOFTWARE\\WOW6432Node\\Autodesk\\ADSKTextureLibraryNew");
            if (lib != null && lib.LibPaths.Count > 0)
            {
                lib.Type = MaterialLibType.Generic;
                libs.Add(lib);
            }

            lib = ParseLibRegistry("SOFTWARE\\WOW6432Node\\Autodesk\\ADSKPrismTextureLibraryNew");
            if (lib != null && lib.LibPaths.Count > 0)
            {
                lib.Type = MaterialLibType.Prism;
                libs.Add(lib);
            }

            return libs;
        }

        private MaterialLib ParseLibRegistry(string libRegistryPath)
        {
            var materialLib = new MaterialLib();
            using (var libKey = Registry.LocalMachine.OpenSubKey(libRegistryPath))
            {
                if (libKey == null) return null;

                foreach (var resolutionName in libKey.GetSubKeyNames())
                {
                    using (var resolutionKey = libKey.OpenSubKey(resolutionName))
                    {
                        if (resolutionKey == null) continue;

                        // Use RevitContext.VersionNumber
                        using (var versionKey = resolutionKey.OpenSubKey(RevitContext.VersionNumber))
                        {
                            if (versionKey == null) continue;

                            var libPath = versionKey.GetValue("LibraryPaths") as string;
                            if (string.IsNullOrEmpty(libPath) || !Directory.Exists(libPath)) continue;

                            if (int.TryParse(resolutionName, out int res))
                            {
                                materialLib.LibPaths.Add(new KeyValuePair<MaterialLibResolution, string>(
                                    (MaterialLibResolution)res, libPath));
                            }
                        }
                    }
                }
            }

            // Sort descending so High (3) is checked before Low (1)
            materialLib.LibPaths = materialLib.LibPaths.OrderByDescending(x => x.Key).ToList();

            return materialLib;
        }

        public string GetAbsoluteTexturePath(string rawTexturePath)
        {
            EnsureInitialized();

            if (_materialLibs == null || _materialLibs.Count <= 0) return null;
            if (string.IsNullOrEmpty(rawTexturePath)) return null;

            string[] rawPaths = rawTexturePath.Split('|');
            for (int index = 0; index < rawPaths.Length; index++)
            {
                var rawPath = rawPaths[index].Trim().Replace("/", "\\");
                if (Path.IsPathRooted(rawPath) && File.Exists(rawPath))
                    return rawPath;

                if (_textureSearchCache.TryGetValue(rawPath, out var cachedPath))
                    return cachedPath;

                string fileName = Path.GetFileName(rawPath);

                // Phase 1: Direct append and 'Mats' directory check
                foreach (var materialLib in _materialLibs.OrderByDescending(l => l.Type))
                {
                    foreach (var libPath in materialLib.LibPaths)
                    {
                        string resolutionStr = ((int)libPath.Key).ToString();
                        string resBasePath = libPath.Value;

                        if (!resBasePath.TrimEnd('\\', '/').EndsWith(resolutionStr))
                        {
                            resBasePath = Path.Combine(resBasePath, resolutionStr);
                        }

                        string absoluteFilePath = Path.Combine(resBasePath, rawPath);
                        if (File.Exists(absoluteFilePath))
                        {
                            _textureSearchCache[rawPath] = absoluteFilePath;
                            return absoluteFilePath;
                        }

                        string matsFilePath = Path.Combine(resBasePath, "Mats", rawPath);
                        if (File.Exists(matsFilePath))
                        {
                            _textureSearchCache[rawPath] = matsFilePath;
                            return matsFilePath;
                        }
                    }
                }

                // Phase 2: Deep search
                foreach (var materialLib in _materialLibs.OrderByDescending(l => l.Type))
                {
                    foreach (var libPath in materialLib.LibPaths)
                    {
                        string resolutionStr = ((int)libPath.Key).ToString();
                        string resBasePath = libPath.Value;
                        if (!resBasePath.TrimEnd('\\', '/').EndsWith(resolutionStr))
                        {
                            resBasePath = Path.Combine(resBasePath, resolutionStr);
                        }

                        try
                        {
                            if (!Directory.Exists(resBasePath)) continue;

                            var foundPath = Directory.EnumerateFiles(resBasePath, fileName, SearchOption.AllDirectories).FirstOrDefault();
                            if (!string.IsNullOrEmpty(foundPath))
                            {
                                _textureSearchCache[rawPath] = foundPath;
                                return foundPath;
                            }
                        }
                        catch (Exception)
                        {
                            // Ignore access exceptions during search
                        }
                    }
                }
            }

            return null;
        }
    }
}
