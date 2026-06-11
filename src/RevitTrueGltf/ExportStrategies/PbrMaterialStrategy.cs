using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Microsoft.Win32;
using RevitTrueGltf.Utils;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RevitTrueGltf.ExportStrategies
{
    // Support types — internal to this file, not part of the public API
    internal enum MaterialLibType { Unknown = 0, Generic = 1, Prism = 2 }
    internal enum MaterialLibResolution { Unknown = 0, Low = 1, Medium = 2, High = 3 }
    internal class MaterialLib
    {
        public MaterialLibType Type;
        public IList<KeyValuePair<MaterialLibResolution, string>> LibPaths
            = new List<KeyValuePair<MaterialLibResolution, string>>();
    }

    /// <summary>
    /// Full PBR material strategy. Parses Revit appearance assets to extract textures,
    /// roughness, metalness, and normal maps, producing a PBR-compatible glTF material.
    /// Falls back to <see cref="ColorOnlyMaterialStrategy"/> when the appearance asset
    /// cannot be recognized (missing, unsupported schema, or texture not found on disk).
    ///
    /// This class absorbs all logic previously contained in MaterialUtils. Texture library
    /// paths are resolved from the Windows registry once at first instantiation, using
    /// <see cref="RevitContext.VersionNumber"/> which must be set before construction.
    /// </summary>
    public class PbrMaterialStrategy : IMaterialStrategy
    {
        private static readonly Vector4 DefaultColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f); // DefaultColor

        // Lazily initialized static cache — populated once across the entire plugin session.
        // Safe because RevitContext.VersionNumber is always set before the first instantiation.
        private static IList<MaterialLib> _materialLibs; // MaterialLibs>

        private readonly IMaterialStrategy _colorFallback = new ColorOnlyMaterialStrategy();

        public PbrMaterialStrategy()
        {
            if (_materialLibs == null)
            {
                _materialLibs = BuildMaterialLibs();
            }
        }

        // ── IMaterialStrategy ──────────────────────────────────────────────────────────

        public MaterialBuildResult Build(MaterialNode node, string materialName)
        {
            if (node == null)
                return _colorFallback.Build(node, materialName);

            var appearance = node.GetAppearance();
            // node.MaterialId == ElementId.InvalidElementId, maybe it is by category
            if (appearance == null || node.MaterialId == ElementId.InvalidElementId)
            {
                return _colorFallback.Build(node, materialName);
            }

            var builder = new MaterialBuilder(materialName);

            // for material similar to Glass
            if (appearance.Name == "GlazingSchema")
            {
                return BuildGlazingMaterial(appearance, builder)
                    ? new MaterialBuildResult { Material = builder, TextureScale = Vector2.One }
                    : _colorFallback.Build(node, materialName);
            }

            // for Masonry
            if (appearance.Name == "MasonryCMUSchema")
            {
                Vector2 masonryScale;
                return BuildMasonryMaterial(appearance, builder, out masonryScale)
                    ? new MaterialBuildResult { Material = builder, TextureScale = masonryScale }
                    : _colorFallback.Build(node, materialName);
            }

            // for Concrete
            if (appearance.Name == "ConcreteSchema")
            {
                Vector2 concreteScale;
                return BuildConcreteMaterial(appearance, builder, out concreteScale)
                    ? new MaterialBuildResult { Material = builder, TextureScale = concreteScale }
                    : _colorFallback.Build(node, materialName);
            }

            var diffuseFadeProperty = appearance.FindByName("generic_diffuse_image_fade");
            float diffuseFade = 1.0f;
            if (diffuseFadeProperty != null)
            {
                var doubleProperty = diffuseFadeProperty as AssetPropertyDouble;
                diffuseFade = (float)doubleProperty.Value;
            }

            // Note on Transparency vs Alpha:
            // In Revit's appearance asset, 'generic_transparency' ranges from 0.0 (fully opaque) to 1.0 (fully transparent).
            // In glTF, the 'alpha' channel ranges from 1.0 (fully opaque) to 0.0 (fully transparent).
            // Therefore, we must invert the value: alpha = 1.0 - transparency.
            var transparencyProperty = appearance.FindByName("generic_transparency");
            float alpha = 1.0f;
            if (transparencyProperty != null)
            {
                var doubleProperty = transparencyProperty as AssetPropertyDouble;
                alpha = 1.0f - (float)doubleProperty.Value;
            }

            var tintColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            var tintToggleProp = appearance.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
            if (tintToggleProp == null || tintToggleProp.Value) // If no toggle, assume true so we try to get tint color
            {
                var tintProperty = appearance.FindByName("common_Tint_color");
                if (tintProperty != null)
                {
                    tintColor = GetColorVector(tintProperty);
                }
            }

            Vector2 textureScale = Vector2.One;
            Vector4 color = DefaultColor;
            var colorProperty = appearance.FindByName("generic_diffuse") ?? appearance.FindByName("opaque_albedo");
            if (colorProperty != null)
            {
                color = GetColorVector(colorProperty);

                // https://jeremytammik.github.io/tbc/a/1596_texture_path.html
                // var test = asset[UnifiedBitmap.UnifiedbitmapBitmap];
                // This line is 2018.1 & up because of the 
                // property reference to UnifiedBitmap
                // .UnifiedbitmapBitmap.  In earlier versions,
                // you can still reference the string name 
                // instead: "unifiedbitmap_Bitmap"
                var textureAsset = FindTextureAsset(colorProperty);
                bool isTextureApplied = false;
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        var absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && File.Exists(absoluteTexturePath))
                        {
                            MemoryImage memoryImage = new MemoryImage(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage, null);
                            builder.WithBaseColor(imageBuilder);
                            isTextureApplied = true;

                            float scaleX = GetTextureScale(textureAsset, "texture_RealWorldScaleX", "unifiedbitmap_RealWorldScaleX");
                            float scaleY = GetTextureScale(textureAsset, "texture_RealWorldScaleY", "unifiedbitmap_RealWorldScaleY");
                            textureScale = new Vector2(scaleX, scaleY);
                        }
                    }
                }

                color = isTextureApplied
                    ? RevitDiffuseColorToGltfBaseColor(color, alpha, diffuseFade)
                    : new Vector4(color.X, color.Y, color.Z, alpha);
            }
            else
            {
                color = new Vector4(node.Color.Red / 255f, node.Color.Green / 255f, node.Color.Blue / 255f, alpha);
            }

            // Apply tint color to the final color (creates a multiplied tint effect)
            color = new Vector4(color.X * tintColor.X, color.Y * tintColor.Y, color.Z * tintColor.Z, color.W);
            builder.WithBaseColor(color);

            if (color.W < 1.0f)
            {
                builder.WithAlpha(AlphaMode.BLEND);
            }

            // Roughness
            float? roughness = null;
            var glossinessProp = appearance.FindByName("generic_glossiness") as AssetPropertyDouble;
            if (glossinessProp != null)
            {
                roughness = 1.0f - (float)glossinessProp.Value / 100.0f;
            }
            else
            {
                var roughnessProp = appearance.FindByName("roughness_standard") as AssetPropertyDouble;
                if (roughnessProp != null)
                    roughness = (float)roughnessProp.Value;
            }

            // Metalness
            var metalProp = appearance.FindByName("generic_is_metal") as AssetPropertyBoolean;
            if (metalProp != null)
            {
                builder.WithMetallicRoughness(metalProp.Value ? 1.0f : 0.0f, roughness);
            }
            else
            {
                var metalValueProp = appearance.FindByName("metal_f0") as AssetPropertyDouble;
                if (metalValueProp != null)
                    builder.WithMetallicRoughness((float)metalValueProp.Value, roughness);
            }

            var bumpProp = appearance.FindByName("generic_bump_map"); // ?? appearance.FindByName("surface_normal")) as AssetPropertyString
            if (bumpProp != null)
            {
                var textureAsset = FindTextureAsset(bumpProp);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        string absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && File.Exists(absoluteTexturePath))
                        {
                            // Convert the height/bump map to a proper tangent-space normal map and get it as a MemoryImage
                            MemoryImage memoryImage = BumpToNormalConverter.Convert(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage);
                            builder.WithNormal(imageBuilder);
                        }
                    }
                }
            }

            return new MaterialBuildResult { Material = builder, TextureScale = textureScale };
        }

        // ── Glazing ────────────────────────────────────────────────────────────────────

        private bool BuildGlazingMaterial(Asset asset, MaterialBuilder materialBuilder)
        {
            double[] baseColor = new double[] { 1.0, 1.0, 1.0 }; // Default white glass
            var transColorProp = asset.FindByName("glazing_transmittance_color") as AssetPropertyDoubleArray4d;
            if (transColorProp != null)
            {
                var transColor = transColorProp.GetValueAsDoubles();
                baseColor[0] = transColor[0];
                baseColor[1] = transColor[1];
                baseColor[2] = transColor[2];
            }

            // Check if Tint is enabled
            var tintToggleProp = asset.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
            if (tintToggleProp != null && tintToggleProp.Value)
            {
                var tintColorProp = asset.FindByName("common_Tint_color") as AssetPropertyDoubleArray4d;
                if (tintColorProp != null)
                {
                    // If Tint is enabled, the tint color usually overrides the transmittance color
                    var tintColor = tintColorProp.GetValueAsDoubles();
                    baseColor[0] = tintColor[0];
                    baseColor[1] = tintColor[1];
                    baseColor[2] = tintColor[2];
                }
            }

            // Get reflectance
            double reflectance = 0.1;
            var reflectanceProp = asset.FindByName("glazing_reflectance") as AssetPropertyDouble;
            if (reflectanceProp != null) reflectance = reflectanceProp.Value;

            // Get the number of glass panes (affects the sense of thickness)
            int levels = 1;
            var levelsProp = asset.FindByName("glazing_no_levels") as AssetPropertyInteger;
            if (levelsProp != null) levels = levelsProp.Value;

            // Levels and Reflectance both affect transparency (Alpha): more panes and higher reflectance lead to a more solid appearance
            // Assuming a single pane's transmittance is 0.8, overlapping multiple panes gradually decreases transmittance
            double singlePaneTransmittance = 0.8;
            double overallTransmittance = Math.Pow(singlePaneTransmittance, levels);
            // Considering the energy carried away by reflected light, the reflectance also enhances the solidity of the base color representation during Alpha blending
            double alpha = 1.0 - overallTransmittance * (1.0 - reflectance);
            alpha = Math.Max(0.0, Math.Min(1.0, alpha));

            // 3. Build glTF material
            materialBuilder
                .WithDoubleSide(true)
                // Switch to Metallic/Roughness workflow
                .WithMetallicRoughnessShader()
                // Set BaseColor - default to a certain transparency level as a fallback if Transmission is not supported
                .WithBaseColor(new Vector4((float)baseColor[0], (float)baseColor[1], (float)baseColor[2], (float)alpha))
                .WithAlpha(AlphaMode.BLEND)
                // Dielectric/glass
                .WithMetallicRoughness(0.0f, 0.05f);

            try
            {
                // Set physical transmission (Transmission extension)
                materialBuilder.WithTransmission(null, (float)overallTransmittance);
            }
            catch { }

            return true;
        }

        // ── Masonry ────────────────────────────────────────────────────────────────────
        private bool BuildMasonryMaterial(Asset asset, MaterialBuilder materialBuilder, out Vector2 textureScale)
        {
            textureScale = Vector2.One;
            Vector4 color = DefaultColor;
            var colorProperty = asset.FindByName("masonrycmu_color");
            bool isTextureApplied = false;

            if (colorProperty != null)
            {
                color = GetColorVector(colorProperty);

                var textureAsset = FindTextureAsset(colorProperty);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        var absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && File.Exists(absoluteTexturePath))
                        {
                            MemoryImage memoryImage = new MemoryImage(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage, null);
                            materialBuilder.WithBaseColor(imageBuilder);
                            isTextureApplied = true;

                            float scaleX = GetTextureScale(textureAsset, "texture_RealWorldScaleX", "unifiedbitmap_RealWorldScaleX");
                            float scaleY = GetTextureScale(textureAsset, "texture_RealWorldScaleY", "unifiedbitmap_RealWorldScaleY");
                            textureScale = new Vector2(scaleX, scaleY);
                        }
                    }
                }
            }

            var tintColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            var tintToggleProp = asset.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
            if (tintToggleProp != null && tintToggleProp.Value)
            {
                var tintProperty = asset.FindByName("common_Tint_color");
                if (tintProperty != null)
                {
                    tintColor = GetColorVector(tintProperty);
                }
            }

            // Apply tint color
            color = new Vector4(color.X * tintColor.X, color.Y * tintColor.Y, color.Z * tintColor.Z, color.W);

            if (isTextureApplied)
            {
                materialBuilder.WithBaseColor(tintColor);
            }
            else
            {
                materialBuilder.WithBaseColor(color);
            }

            // Roughness / Metalness: Non-metallic, typically rough (roughness = 0.8)
            materialBuilder.WithMetallicRoughness(0.0f, 0.8f);

            // Bump map / Normal map
            var bumpProp = asset.FindByName("masonrycmu_pattern_map");
            if (bumpProp != null)
            {
                var textureAsset = FindTextureAsset(bumpProp);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        string absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && File.Exists(absoluteTexturePath))
                        {
                            MemoryImage memoryImage = BumpToNormalConverter.Convert(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage);
                            materialBuilder.WithNormal(imageBuilder);
                        }
                    }
                }
            }

            return true;
        }

        // ── Concrete ───────────────────────────────────────────────────────────────────
        private bool BuildConcreteMaterial(Asset asset, MaterialBuilder materialBuilder, out Vector2 textureScale)
        {
            textureScale = Vector2.One;
            Vector4 color = DefaultColor;
            var colorProperty = asset.FindByName("concrete_color");
            bool isTextureApplied = false;

            if (colorProperty != null)
            {
                color = GetColorVector(colorProperty);

                var textureAsset = FindTextureAsset(colorProperty);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        var absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && File.Exists(absoluteTexturePath))
                        {
                            MemoryImage memoryImage = new MemoryImage(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage, null);
                            materialBuilder.WithBaseColor(imageBuilder);
                            isTextureApplied = true;

                            float scaleX = GetTextureScale(textureAsset, "texture_RealWorldScaleX", "unifiedbitmap_RealWorldScaleX");
                            float scaleY = GetTextureScale(textureAsset, "texture_RealWorldScaleY", "unifiedbitmap_RealWorldScaleY");
                            textureScale = new Vector2(scaleX, scaleY);
                        }
                    }
                }
            }

            var tintColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            var tintToggleProp = asset.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
            if (tintToggleProp != null && tintToggleProp.Value)
            {
                var tintProperty = asset.FindByName("common_Tint_color");
                if (tintProperty != null)
                {
                    tintColor = GetColorVector(tintProperty);
                }
            }

            // Apply tint color
            color = new Vector4(color.X * tintColor.X, color.Y * tintColor.Y, color.Z * tintColor.Z, color.W);

            if (isTextureApplied)
            {
                materialBuilder.WithBaseColor(tintColor);
            }
            else
            {
                materialBuilder.WithBaseColor(color);
            }

            // Roughness / Metalness: Non-metallic, typically rough (roughness = 0.8)
            materialBuilder.WithMetallicRoughness(0.0f, 0.8f);

            // Bump map / Normal map
            var bumpProp = asset.FindByName("concrete_bump_map") ?? asset.FindByName("concrete_bm_map");
            if (bumpProp != null)
            {
                var textureAsset = FindTextureAsset(bumpProp);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        string absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && File.Exists(absoluteTexturePath))
                        {
                            MemoryImage memoryImage = BumpToNormalConverter.Convert(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage);
                            materialBuilder.WithNormal(imageBuilder);
                        }
                    }
                }
            }

            return true;
        }

        // ── Texture Library Initialization ─────────────────────────────────────────────
        private static IList<MaterialLib> BuildMaterialLibs()
        {
            var libs = new List<MaterialLib>();

            //HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Autodesk\ADSKTextureLibraryNew
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

        private static MaterialLib ParseLibRegistry(string libRegistryPath)
        {
            var materialLib = new MaterialLib();
            var libKey = Registry.LocalMachine.OpenSubKey(libRegistryPath);
            if (libKey == null) return null;

            foreach (var resolutionName in libKey.GetSubKeyNames())
            {
                var resolutionKey = libKey.OpenSubKey(resolutionName);
                if (resolutionKey == null) continue;

                // Use RevitContext.VersionNumber (set by ExportGltfCommand before first instantiation)
                var versionKey = resolutionKey.OpenSubKey(RevitContext.VersionNumber);
                if (versionKey == null) continue;

                var libPath = versionKey.GetValue("LibraryPaths") as string;
                if (string.IsNullOrEmpty(libPath) || !Directory.Exists(libPath)) continue;

                materialLib.LibPaths.Add(new KeyValuePair<MaterialLibResolution, string>(
                    (MaterialLibResolution)int.Parse(resolutionName), libPath));
            }

            // Sort descending so High (3) is checked before Low (1)
            materialLib.LibPaths = materialLib.LibPaths.OrderByDescending(x => x.Key).ToList();

            return materialLib;
        }

        // ── Private Helpers ────────────────────────────────────────────────────────────

        private Vector4 GetColorVector(AssetProperty prop)
        {
            if (prop is AssetPropertyDoubleArray4d colorProp)
            {
                var color = colorProp.GetValueAsColor();
                return new Vector4(color.Red / 255.0f, color.Green / 255f, color.Blue / 255f, 1.0f);
            }
            return new Vector4(1f, 1f, 1f, 1f);
        }


        private static readonly Dictionary<string, string> _textureSearchCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string GetAbsoluteTexturePath(string rawTexturePath)
        {
            if (_materialLibs == null || _materialLibs.Count <= 0) return null;
            if (string.IsNullOrEmpty(rawTexturePath)) return null;

            // 1. may contain multiple paths separated by '|'
            string[] rawPaths = rawTexturePath.Split('|');
            for (int index = 0; index < rawPaths.Length; index++)
            {
                var rawPath = rawPaths[index].Trim().Replace("/", "\\");
                if (Path.IsPathRooted(rawPath) && File.Exists(rawPath))
                    return rawPath;

                // Check cache first
                if (_textureSearchCache.TryGetValue(rawPath, out var cachedPath))
                    return cachedPath;

                string fileName = Path.GetFileName(rawPath);

                // Priority 1: Prism -> Generic
                // Priority 2: Resolution 3 -> 2 -> 1
                // First attempt: Direct path combination and common subdirectories
                foreach (var materialLib in _materialLibs.OrderByDescending(l => l.Type))
                {
                    foreach (var libPath in materialLib.LibPaths)
                    {
                        string resolutionStr = ((int)libPath.Key).ToString();
                        string resBasePath = libPath.Value;

                        // If the registry path doesn't already end with the resolution folder (1, 2, or 3), append it.
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

                        // Try "Mats" subdirectory directly to skip deep search
                        string matsFilePath = Path.Combine(resBasePath, "Mats", rawPath);
                        if (File.Exists(matsFilePath))
                        {
                            _textureSearchCache[rawPath] = matsFilePath;
                            return matsFilePath;
                        }
                    }
                }

                // Second attempt: Deep search in all libraries
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

        private Vector4 RevitDiffuseColorToGltfBaseColor(Vector4 diffuseColor, float alpha, float diffuseFade)
        {
            if (diffuseFade >= 0.99f)
            {
                // Case A: should set texture
                // gltfMaterial.BaseColorTexture = diffuseTexture; // Assign texture
                return new Vector4(1.0f, 1.0f, 1.0f, alpha);
            }
            else if (diffuseFade <= 0.01f)
            {
                // Case B: no texture
                return new Vector4(diffuseColor.X, diffuseColor.Y, diffuseColor.Z, alpha);
            }
            else
            {
                // Case C: Mixed and should set texture
                // BaseColor = RevitColor * (1 - diffuseFade) + White(1.0) * diffuseFade
                return new Vector4(
                    diffuseColor.X * (1.0f - diffuseFade) + 1.0f * diffuseFade, // R
                    diffuseColor.Y * (1.0f - diffuseFade) + 1.0f * diffuseFade, // G
                    diffuseColor.Z * (1.0f - diffuseFade) + 1.0f * diffuseFade, // B
                    alpha // A remains unchanged
                );
            }
        }

        private Asset FindTextureAsset(AssetProperty assetProperty)
        {
            if (assetProperty == null) return null;

            if (assetProperty.Type == AssetPropertyType.Asset)
            {
                var asset = assetProperty as Asset;
                var assetTypeProp = asset.FindByName("assettype");
                if (assetTypeProp != null && (assetTypeProp as AssetPropertyString).Value == "texture")
                {
                    return asset;
                }
                return FindTextureAsset(asset);
            }
            else
            {
                for (int i = 0; i < assetProperty.NumberOfConnectedProperties; i++)
                {
                    var textureAsset = FindTextureAsset(assetProperty.GetConnectedProperty(i));
                    if (textureAsset != null) return textureAsset;
                }
                return null;
            }
        }

        private Asset FindTextureAsset(Asset asset)
        {
            if (asset == null) return null;

            var assetTypeProp = asset.FindByName("assettype");
            if (assetTypeProp != null && (assetTypeProp as AssetPropertyString).Value == "texture")
            {
                return asset;
            }

            for (int i = 0; i < asset.Size; i++)
            {
                var textureAsset = FindTextureAsset(asset[i]);
                if (textureAsset != null) return textureAsset;
            }

            return null;
        }

        private float GetTextureScale(Asset textureAsset, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var prop = textureAsset.FindByName(propertyName);
                if (prop != null)
                {
                    if (prop is AssetPropertyDistance distProp)
                    {
#if REVIT2020
                        return (float)Autodesk.Revit.DB.UnitUtils.ConvertToInternalUnits(distProp.Value, distProp.DisplayUnitType);
#else
                        return (float)Autodesk.Revit.DB.UnitUtils.ConvertToInternalUnits(distProp.Value, distProp.GetUnitTypeId());
#endif
                    }
                    else if (prop is AssetPropertyDouble doubleProp)
                    {
                        return (float)doubleProp.Value;
                    }
                }
            }
            return 1.0f;
        }

        private static void DumpAsset(Asset asset)
        {
            Debug.WriteLine($"=== Asset Dump: {asset.Name} ({asset.Size} properties) ===");
            for (int i = 0; i < asset.Size; i++)
            {
                DumpAssetProperty(asset[i], 0);
            }
            Debug.WriteLine("========================================");
        }

        private static void DumpAssetProperty(AssetProperty prop, int indentLevel)
        {
            if (prop == null) return;
            string indent = new string(' ', indentLevel * 2);
            string typeName = prop.GetType().Name;
            string propName = prop.Name;
            string valueStr = "";

            try
            {
                if (prop is AssetPropertyDistance distProp)
                {
#if REVIT2020
                    valueStr = $"{distProp.Value} (Unit: {distProp.DisplayUnitType})";
#else
                    valueStr = $"{distProp.Value} (Unit: {distProp.GetUnitTypeId().TypeId})";
#endif
                }
                else if (prop is AssetPropertyString stringProp)
                {
                    valueStr = stringProp.Value;
                }
                else if (prop is AssetPropertyDouble doubleProp)
                {
                    valueStr = doubleProp.Value.ToString();
                }
                else if (prop is AssetPropertyInteger intProp)
                {
                    valueStr = intProp.Value.ToString();
                }
                else if (prop is AssetPropertyBoolean boolProp)
                {
                    valueStr = boolProp.Value.ToString();
                }
                else if (prop is AssetPropertyDoubleArray4d double4dProp)
                {
                    var vals = double4dProp.GetValueAsDoubles();
                    valueStr = "[" + string.Join(", ", vals) + "]";
                }
                else if (prop is AssetPropertyList listProp)
                {
                    var list = listProp.GetValue();
                    Debug.WriteLine($"{indent}- Property: {propName} (Type: {typeName}, List size: {list.Count})");
                    foreach (var subProp in list)
                    {
                        DumpAssetProperty(subProp, indentLevel + 1);
                    }
                    return;
                }
                else if (prop is Asset childAsset)
                {
                    Debug.WriteLine($"{indent}- Property: {propName} (Type: {typeName}, Sub-Asset Name: {childAsset.Name})");
                    for (int i = 0; i < childAsset.Size; i++)
                    {
                        DumpAssetProperty(childAsset[i], indentLevel + 1);
                    }
                    return;
                }
                else
                {
                    // Fallback using reflection to find a Value property if present
                    var valuePropInfo = prop.GetType().GetProperty("Value");
                    if (valuePropInfo != null)
                    {
                        valueStr = valuePropInfo.GetValue(prop)?.ToString() ?? "null";
                    }
                    else
                    {
                        valueStr = $"[{typeName}]";
                    }
                }
            }
            catch (Exception ex)
            {
                valueStr = $"[Error reading: {ex.Message}]";
            }

            Debug.WriteLine($"{indent}- Property: {propName} (Type: {typeName}) = {valueStr}");

            // Also check connected properties recursively
            try
            {
                if (prop.NumberOfConnectedProperties > 0)
                {
                    Debug.WriteLine($"{indent}  Connected Properties ({prop.NumberOfConnectedProperties}):");
                    for (int i = 0; i < prop.NumberOfConnectedProperties; i++)
                    {
                        DumpAssetProperty(prop.GetConnectedProperty(i), indentLevel + 1);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{indent}  [Error reading connected properties: {ex.Message}]");
            }
        }
    }
}
