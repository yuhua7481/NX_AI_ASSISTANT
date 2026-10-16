namespace NXAI.Assistant.Core;

/// <summary>
/// The create_block tool contract: the single modeling tool exposed to the
/// model in Phase 2. The schema below is the authoritative field list the
/// parser (CreateBlockRequestParser) enforces — both must stay in sync.
/// </summary>
public static class CreateBlockTool
{
    public const string Name = CreateBlockRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["length", "width", "height", "unit"],
          "properties": {
            "length": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "长方体 X 方向长度，按 unit 指定的单位。"
            },
            "width": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "长方体 Y 方向宽度，按 unit 指定的单位。"
            },
            "height": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "长方体 Z 方向高度，按 unit 指定的单位。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "尺寸单位。执行时会统一换算为毫米。"
            },
            "origin_x": {
              "type": "number",
              "description": "可选。长方体角点原点 X 坐标（毫米），省略时为 0。"
            },
            "origin_y": {
              "type": "number",
              "description": "可选。长方体角点原点 Y 坐标（毫米），省略时为 0。"
            },
            "origin_z": {
              "type": "number",
              "description": "可选。长方体角点原点 Z 坐标（毫米），省略时为 0。"
            }
          }
        }
        """;

    public const string Description =
        "在当前 NX 活动工作部件中创建一个轴对齐长方体（Block）。" +
        "需要 length/width/height 与 unit；可选 origin_x/origin_y/origin_z 指定角点原点（毫米，默认 0,0,0）。" +
        "仅当用户明确要求创建长方体时调用；调用后需要用户在界面上确认才会执行。";

    /// <summary>Builds a registry preloaded with all Phase 2/3/4 geometry tools.</summary>
    public static ToolRegistry CreateRegistry() => CreateRegistryWithDefaults();

    internal static ToolRegistry CreateRegistryWithDefaults()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(Name, Description, ParametersJsonSchema));
        registry.Register(new ToolDefinition(
            CreateCylinderTool.Name, CreateCylinderTool.Description,
            CreateCylinderTool.ParametersJsonSchema));
        registry.Register(new ToolDefinition(
            CreateConeTool.Name, CreateConeTool.Description,
            CreateConeTool.ParametersJsonSchema));
        registry.Register(new ToolDefinition(
            CreateSphereTool.Name, CreateSphereTool.Description,
            CreateSphereTool.ParametersJsonSchema));
        return registry;
    }
}
