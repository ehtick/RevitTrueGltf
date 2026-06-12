using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using RevitTrueGltf.ExportStrategies.Schemas;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RevitTrueGltf.ExportStrategies
{
    /// <summary>
    /// Serves as the dispatcher for PBR material strategies.
    /// It delegates the actual parsing logic to specific schema strategies based on the material's appearance asset name.
    /// </summary>
    public class PbrMaterialStrategy : IMaterialStrategy
    {
        private readonly IMaterialStrategy _colorFallback = new ColorOnlyMaterialStrategy();

        private readonly DefaultSchemaStrategy _defaultStrategy;
        private readonly Dictionary<string, SchemaExportStrategy> _strategies;

        public PbrMaterialStrategy()
        {
            // Ensure texture library paths are resolved once
            TextureLibraryResolver.Instance.EnsureInitialized();

            _defaultStrategy = new DefaultSchemaStrategy();

            _strategies = new Dictionary<string, SchemaExportStrategy>
            {
                { "GlazingSchema", new GlazingSchemaStrategy() },
                { "SolidGlassSchema", new GlazingSchemaStrategy() }
            };
        }

        public MaterialBuildResult Build(MaterialNode node, string materialName)
        {
            if (node == null)
                return _colorFallback.Build(node, materialName);

            var appearance = node.GetAppearance();
            if (appearance == null || node.MaterialId == ElementId.InvalidElementId)
            {
                return _colorFallback.Build(node, materialName);
            }

            DumpAsset(appearance);

            if (_strategies.TryGetValue(appearance.Name, out var strategy))
            {
                return strategy.Build(node, materialName, appearance) ?? _colorFallback.Build(node, materialName);
            }

            return _defaultStrategy.Build(node, materialName, appearance) ?? _colorFallback.Build(node, materialName);
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
