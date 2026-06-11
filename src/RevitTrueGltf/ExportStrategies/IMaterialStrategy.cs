using Autodesk.Revit.DB;
using SharpGLTF.Materials;
using System.Numerics;

namespace RevitTrueGltf.ExportStrategies
{
    public class MaterialBuildResult
    {
        public MaterialBuilder Material { get; set; }
        public Vector2 TextureScale { get; set; } = Vector2.One;
    }

    /// <summary>
    /// Converts a Revit MaterialNode into a glTF MaterialBuilder along with texture scaling information.
    /// 
    /// A single strategy instance is created per export session in ExportGltfContext.Start()
    /// and reused for every OnMaterial() call. Results are cached by the context via
    /// _materialBuilderCache, so Build() is invoked at most once per unique material.
    /// </summary>
    public interface IMaterialStrategy
    {
        MaterialBuildResult Build(MaterialNode node, string materialName);
    }
}
