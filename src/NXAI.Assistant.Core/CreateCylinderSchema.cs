namespace NXAI.Assistant.Core;

/// <summary>
/// The create_cylinder tool contract: creates an axis-aligned cylinder along
/// the work part's +Z axis. The schema below is the authoritative field list
/// the parser (CreateCylinderRequestParser) enforces — both must stay in sync.
/// </summary>
public static class CreateCylinderTool
{
    public const string Name = CreateCylinderRequestParser.ToolName;

    public const string ParametersJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["diameter", "height", "unit"],
          "properties": {
            "diameter": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "圆柱直径，按 unit 指定的单位。"
            },
            "height": {
              "type": "number",
              "exclusiveMinimum": 0,
              "description": "圆柱高度，沿 +Z 轴方向，按 unit 指定的单位。"
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
            },
            "placement": {
              "type": "string",
              "enum": ["global_origin", "selected_face_center"],
              "description": "可选。放置模式：global_origin（默认）按 origin 坐标与 +Z 轴创建；selected_face_center 在用户当前选中的单个平面中心、沿该面外法向创建（此时 origin 被忽略）。仅当用户明确要求在选中面上创建时使用 selected_face_center。"
            }
          }
        }
        """;

    public const string Description =
        "在当前 NX 活动工作部件中创建一个圆柱体（轴线沿 Z 轴正方向，origin 为底面圆心）。" +
        "需要 diameter/height 与 unit；可选 origin_x/origin_y/origin_z 指定底面圆心（毫米，默认 0,0,0）；可选 placement 选择放置模式（默认 global_origin；用户要求在选中面上创建时用 selected_face_center）。" +
        "仅当用户明确要求创建圆柱体时调用；调用后需要用户在界面上确认才会执行。";
}
