using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using System.Numerics;

namespace RevitTrueGltf.ExportStrategies.Schemas
{
    public abstract class SchemaExportStrategy
    {
        protected static readonly Vector4 DefaultColor = new Vector4(0.8f, 0.8f, 0.8f, 1.0f);

        public abstract MaterialBuildResult Build(MaterialNode node, string materialName, Asset appearance);

        protected AssetProperty FindFirstProperty(Asset asset, params string[] propertyNames)
        {
            if (propertyNames == null)
            {
                return null;
            }

            foreach (var name in propertyNames)
            {
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                var prop = asset.FindByName(name);
                if (prop != null) return prop;
            }
            return null;
        }

        protected Vector4 GetColorVector(AssetProperty prop)
        {
            if (prop is AssetPropertyDoubleArray4d colorProp)
            {
                var color = colorProp.GetValueAsColor();
                return new Vector4(color.Red / 255.0f, color.Green / 255f, color.Blue / 255f, 1.0f);
            }
            return new Vector4(1f, 1f, 1f, 1f);
        }

        protected string GetAbsoluteTexturePath(string rawTexturePath)
        {
            return TextureLibraryResolver.Instance.GetAbsoluteTexturePath(rawTexturePath);
        }

        protected Vector4 RevitDiffuseColorToGltfBaseColor(Vector4 diffuseColor, float alpha, float diffuseFade)
        {
            // Revit rendering equation: FinalColor = Lerp(diffuseColor, Texture, diffuseFade)
            // glTF rendering equation: FinalColor = BaseColorFactor * Texture
            // We approximate this mathematical mismatch by setting BaseColorFactor = Lerp(diffuseColor, White, diffuseFade).
            // - If diffuseFade = 1.0 (Full Texture): Factor = White. glTF shows pure Texture (perfect match).
            // - If diffuseFade = 0.0 (No Texture): Factor = diffuseColor. glTF shows Texture tinted by diffuseColor.
            // (Note: glTF cannot completely drop the texture dynamically without removing the texture map entirely, 
            // but this color multiplier approximation is the industry standard approach for glTF conversion).

            if (diffuseFade >= 0.99f)
            {
                return new Vector4(1.0f, 1.0f, 1.0f, alpha);
            }
            else if (diffuseFade <= 0.01f)
            {
                return new Vector4(diffuseColor.X, diffuseColor.Y, diffuseColor.Z, alpha);
            }
            else
            {
                float r = diffuseColor.X * (1.0f - diffuseFade) + 1.0f * diffuseFade;
                float g = diffuseColor.Y * (1.0f - diffuseFade) + 1.0f * diffuseFade;
                float b = diffuseColor.Z * (1.0f - diffuseFade) + 1.0f * diffuseFade;

                return new Vector4(r, g, b, alpha);
            }
        }

        protected Asset FindTextureAsset(AssetProperty assetProperty)
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

        protected Asset FindTextureAsset(Asset asset)
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

        protected float GetTextureScale(Asset textureAsset, params string[] propertyNames)
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
    }
}
