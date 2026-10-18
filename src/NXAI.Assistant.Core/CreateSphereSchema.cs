namespace NXAI.Assistant.Core;

/// <summary>
/// The create_sphere tool contract: creates a real spherical solid centered
/// at origin. Schema is the authoritative field list the parser
/// (CreateSphereRequestParser) enforces — both must stay in sync.
/// </summary>
public static class CreateSphereTool
{
    public const string Name = CreateSphereRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["diameter", "unit"],
          "properties": {
            "diameter": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "球体直径，按 unit 指定的单位。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "尺寸单位。执行时会统一换算为毫米。"
            },
            "origin_x": {
              "type": "number",
              "description": "可选。球心 X 坐标（毫米），省略时为 0。"
            },
            "origin_y": {
              "type": "number",
              "description": "可选。球心 Y 坐标（毫米），省略时为 0。"
            },
            "origin_z": {
              "type": "number",
              "description": "可选。球心 Z 坐标（毫米），省略时为 0。"
            }
          }
        }
        """;

    public const string Description =
        "在当前 NX 活动工作部件中创建一个球体（真实球形实体，非网格近似）。" +
        "需要 diameter 与 unit；可选 origin_x/origin_y/origin_z 指定球心（毫米，默认 0,0,0）。" +
        "仅当用户明确要求创建球体时调用；调用后需要用户在界面上确认才会执行。";
}
