using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitTrueGltf.Utils;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using System.Collections.Generic;
using System.Numerics;

namespace RevitTrueGltf.ExportStrategies.Schemas
{
    public class SchemaConfig
    {
        public string ColorProperty { get; set; }
        public string BumpProperty { get; set; }
        /// <summary>Schema-specific property name for diffuse image fade (0=pure texture, 1=pure white). Null means no such property.</summary>
        public string DiffuseFadeProperty { get; set; }
        /// <summary>Schema-specific property name for transparency (0=opaque, 1=fully transparent). Null means no such property.</summary>
        public string TransparencyProperty { get; set; }
        public float DefaultRoughness { get; set; }
        public float DefaultMetallic { get; set; }
    }

    public class DefaultSchemaStrategy : SchemaExportStrategy
    {
        private static readonly string[] ColorSuffixes = { "color", "diffuse", "base_color" };
        private static readonly string[] BumpSuffixes = { "bump_map", "pattern_map", "bm_map" };

        private static readonly Dictionary<string, SchemaConfig> Configs = new Dictionary<string, SchemaConfig>
        {
            { "GenericSchema",      new SchemaConfig { ColorProperty = "generic_diffuse",            BumpProperty = "generic_bump_map",           DiffuseFadeProperty = "generic_diffuse_image_fade", TransparencyProperty = "generic_transparency", DefaultRoughness = 0.5f, DefaultMetallic = 0.0f } },
            { "ConcreteSchema",     new SchemaConfig { ColorProperty = "concrete_color",             BumpProperty = "concrete_bump_map",          DefaultRoughness = 0.8f, DefaultMetallic = 0.0f } },
            { "MasonryCMUSchema",   new SchemaConfig { ColorProperty = "masonrycmu_color",           BumpProperty = "masonrycmu_pattern_map",      DefaultRoughness = 0.8f, DefaultMetallic = 0.0f } },
            { "WoodSchema",         new SchemaConfig { ColorProperty = "wood_color",                 BumpProperty = "wood_bump_map",              DefaultRoughness = 0.6f, DefaultMetallic = 0.0f } },
            // HardwoodSchema: hardwood_color is AssetPropertyReference (pure texture pointer, no embedded color value).
            // BumpProperty is null because hardwood_imperfections_shader has no actual bitmap path.
            { "HardwoodSchema",     new SchemaConfig { ColorProperty = "hardwood_color",             BumpProperty = null,                         DefaultRoughness = 0.3f, DefaultMetallic = 0.0f } },
            // MetalSchema: metal_pattern_map does not exist; the real property is metal_pattern_shader but its
            // unifiedbitmap_Bitmap is empty, so BumpProperty is null to avoid fruitless lookups.
            { "MetalSchema",        new SchemaConfig { ColorProperty = "metal_color",                BumpProperty = null,                         DefaultRoughness = 0.2f, DefaultMetallic = 1.0f } },
            { "MetallicPaintSchema",new SchemaConfig { ColorProperty = "metallicpaint_base_color",   BumpProperty = "metallicpaint_finish_bumps", DefaultRoughness = 0.1f, DefaultMetallic = 0.8f } }
        };

        private string[] GetFallbackPropertyNames(string prefix, string[] suffixes)
        {
            var names = new List<string>();
            if (!string.IsNullOrEmpty(prefix) && suffixes != null)
            {
                foreach (var suffix in suffixes)
                {
                    names.Add($"{prefix}_{suffix}");
                }
            }
            return names.ToArray();
        }

        public override MaterialBuildResult Build(MaterialNode node, string materialName, Asset appearance)
        {
            var builder = new MaterialBuilder(materialName);
            string prefix = appearance.Name.Replace("Schema", "").ToLower();
            Configs.TryGetValue(appearance.Name, out var config);

            // ==========================================
            // 1. Get Color Property
            // ==========================================
            var colorProp = FindFirstProperty(appearance, config?.ColorProperty);

            if (colorProp == null)
            {
                var guessNames = GetFallbackPropertyNames(prefix, ColorSuffixes);
                colorProp = FindFirstProperty(appearance, guessNames);
            }

            if (colorProp == null)
            {
                colorProp = FindFirstProperty(appearance, "generic_diffuse", "opaque_albedo");
            }

            // ==========================================
            // 2. Parse Color & Texture
            // ==========================================
            Vector2 textureScale = Vector2.One;
            Vector4 color = DefaultColor;
            bool isTextureApplied = false;

            // Handle diffuse fade and transparency using schema-specific property names (avoids cross-schema FindByName calls)
            var diffuseFadeProperty = FindFirstProperty(appearance, config?.DiffuseFadeProperty) as AssetPropertyDouble;
            float diffuseFade = diffuseFadeProperty != null ? (float)diffuseFadeProperty.Value : 1.0f;

            var transparencyProperty = FindFirstProperty(appearance, config?.TransparencyProperty) as AssetPropertyDouble;
            float alpha = transparencyProperty != null ? 1.0f - (float)transparencyProperty.Value : 1.0f;

            if (colorProp != null)
            {
                color = GetColorVector(colorProp);
                var textureAsset = FindTextureAsset(colorProp);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        var absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && System.IO.File.Exists(absoluteTexturePath))
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
                color = isTextureApplied ? RevitDiffuseColorToGltfBaseColor(color, alpha, diffuseFade) : new Vector4(color.X, color.Y, color.Z, alpha);
            }
            else
            {
                color = new Vector4(node.Color.Red / 255f, node.Color.Green / 255f, node.Color.Blue / 255f, alpha);
            }

            // Apply Tint
            var tintColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            var tintToggleProp = appearance.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
            if (tintToggleProp != null && tintToggleProp.Value)
            {
                var tintProperty = appearance.FindByName("common_Tint_color");
                if (tintProperty != null) tintColor = GetColorVector(tintProperty);
            }

            color = new Vector4(color.X * tintColor.X, color.Y * tintColor.Y, color.Z * tintColor.Z, color.W);
            builder.WithBaseColor(color);

            if (color.W < 1.0f)
            {
                builder.WithAlpha(AlphaMode.BLEND);
            }

            // ==========================================
            // 3. Get Bump Property
            // ==========================================
            var bumpProp = FindFirstProperty(appearance, config?.BumpProperty);

            if (bumpProp == null)
            {
                var guessNames = GetFallbackPropertyNames(prefix, BumpSuffixes);
                bumpProp = FindFirstProperty(appearance, guessNames);
            }

            if (bumpProp == null)
            {
                bumpProp = FindFirstProperty(appearance, "generic_bump_map", "surface_normal");
            }

            if (bumpProp != null)
            {
                var textureAsset = FindTextureAsset(bumpProp);
                if (textureAsset != null)
                {
                    var textureProperty = textureAsset.FindByName("unifiedbitmap_Bitmap") as AssetPropertyString;
                    if (textureProperty != null)
                    {
                        string absoluteTexturePath = GetAbsoluteTexturePath(textureProperty.Value);
                        if (!string.IsNullOrEmpty(absoluteTexturePath) && System.IO.File.Exists(absoluteTexturePath))
                        {
                            MemoryImage memoryImage = BumpToNormalConverter.Convert(absoluteTexturePath);
                            ImageBuilder imageBuilder = ImageBuilder.From(memoryImage);
                            builder.WithNormal(imageBuilder);
                        }
                    }
                }
            }

            // ==========================================
            // 4. Handle Physical Properties (Roughness/Metallic)
            // ==========================================
            float roughness = config?.DefaultRoughness ?? 0.5f;
            float metallic = config?.DefaultMetallic ?? 0.0f;

            // Try to extract exact values if they exist
            var glossinessProp = FindFirstProperty(appearance, $"{prefix}_glossiness", "generic_glossiness") as AssetPropertyDouble;
            if (glossinessProp != null)
            {
                roughness = 1.0f - (float)glossinessProp.Value / 100.0f;
            }
            else
            {
                var roughnessProp = FindFirstProperty(appearance, $"{prefix}_roughness", "roughness_standard") as AssetPropertyDouble;
                if (roughnessProp != null) roughness = (float)roughnessProp.Value;
            }

            var metalProp = FindFirstProperty(appearance, $"{prefix}_is_metal", "generic_is_metal") as AssetPropertyBoolean;
            if (metalProp != null)
            {
                metallic = metalProp.Value ? 1.0f : 0.0f;
            }
            else
            {
                var metalValueProp = FindFirstProperty(appearance, $"{prefix}_f0", "metal_f0") as AssetPropertyDouble;
                if (metalValueProp != null) metallic = (float)metalValueProp.Value;
            }

            builder.WithMetallicRoughness(metallic, roughness);

            return new MaterialBuildResult { Material = builder, TextureScale = textureScale };
        }
    }
}
