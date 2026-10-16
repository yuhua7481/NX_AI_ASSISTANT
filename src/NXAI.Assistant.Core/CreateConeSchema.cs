namespace NXAI.Assistant.Core;

/// <summary>
/// The create_cone tool contract: a right circular cone or truncated cone
/// (frustum) along the work part's +Z axis. Schema is the authoritative field
/// list the parser (CreateConeRequestParser) enforces — both must stay in sync.
/// </summary>
public static class CreateConeTool
{
    public const string Name = CreateConeRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["base_diameter", "height", "unit"],
          "properties": {
            "base_diameter": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "圆锥/圆台底面直径，按 unit 指定的单位。"
            },
            "top_diameter": {
              "type": "number",
              "minimum": 0,
              "description": "可选。顶部直径（毫米归一后须小于底面直径）。省略或为 0 时创建标准圆锥（锥顶收敛于轴线）；大于 0 时创建圆台（截头锥体）。"
            },
            "height": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "高度，沿 +Z 轴方向，按 unit 指定的单位。"
            },
            "unit": {
              "type": "string",
              "enum": ["mm", "cm", "m", "inch"],
              "description": "尺寸单位。执行时会统一换算为毫米。"
            },
            "origin_x": {
              "type": "number",
              "description": "可选。底面圆心 X 坐标（毫米），省略时为 0。"
            },
            "origin_y": {
              "type": "number",
              "description": "可选。底面圆心 Y 坐标（毫米），省略时为 0。"
            },
            "origin_z": {
              "type": "number",
              "description": "可选。底面圆心 Z 坐标（毫米），省略时为 0。"
            }
          }
        }
        """;

    public const string Description =
        "在当前 NX 活动工作部件中创建一个正圆锥体或圆台（轴线沿 Z 轴正方向，origin 为底面圆心）。" +
        "需要 base_diameter/height 与 unit；可选 top_diameter（省略或 0 时为标准圆锥，0 < top_diameter < base_diameter 时为圆台）；" +
        "可选 origin_x/origin_y/origin_z 指定底面圆心（毫米，默认 0,0,0）。" +
        "仅当用户明确要求创建圆锥体或圆台时调用；调用后需要用户在界面上确认才会执行。";
}
