using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using SharpGLTF.Materials;
using System;
using System.Numerics;

namespace RevitTrueGltf.ExportStrategies.Schemas
{
    public class GlazingSchemaStrategy : SchemaExportStrategy
    {
        public override MaterialBuildResult Build(MaterialNode node, string materialName, Asset appearance)
        {
            var builder = new MaterialBuilder(materialName);
            double[] baseColor = new double[] { 1.0, 1.0, 1.0 }; // Default white glass
            var transColorProp = appearance.FindByName("glazing_transmittance_color") as AssetPropertyDoubleArray4d;
            if (transColorProp != null)
            {
                var transColor = transColorProp.GetValueAsDoubles();
                baseColor[0] = transColor[0];
                baseColor[1] = transColor[1];
                baseColor[2] = transColor[2];
            }

            // Check if Tint is enabled
            var tintToggleProp = appearance.FindByName("common_Tint_toggle") as AssetPropertyBoolean;
            if (tintToggleProp != null && tintToggleProp.Value)
            {
                var tintColorProp = appearance.FindByName("common_Tint_color") as AssetPropertyDoubleArray4d;
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
            var reflectanceProp = appearance.FindByName("glazing_reflectance") as AssetPropertyDouble;
            if (reflectanceProp != null) reflectance = reflectanceProp.Value;

            // Get the number of glass panes (affects the sense of thickness)
            int levels = 1;
            var levelsProp = appearance.FindByName("glazing_no_levels") as AssetPropertyInteger;
            if (levelsProp != null) levels = levelsProp.Value;

            // Levels and Reflectance both affect transparency (Alpha): more panes and higher reflectance lead to a more solid appearance
            // Assuming a single pane's transmittance is 0.8, overlapping multiple panes gradually decreases transmittance
            double singlePaneTransmittance = 0.8;
            double overallTransmittance = Math.Pow(singlePaneTransmittance, levels);
            // Considering the energy carried away by reflected light, the reflectance also enhances the solidity of the base color representation during Alpha blending
            double alpha = 1.0 - overallTransmittance * (1.0 - reflectance);
            alpha = Math.Max(0.0, Math.Min(1.0, alpha));

            // 3. Build glTF material
            builder
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
                builder.WithTransmission(null, (float)overallTransmittance);
            }
            catch { }

            return new MaterialBuildResult { Material = builder, TextureScale = Vector2.One };
        }
    }
}
