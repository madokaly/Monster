#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

/// <summary>
/// 挂载到怪物 GameObject 上，在编辑器/运行时始终绘制检测范围 Gizmo。
/// 数据由本脚本自身字段维护，也可由 AICanSeeTargetFollowerEntity 在运行时写入。
/// </summary>
[AddComponentMenu("Editor/MonsterDetectionRangeGizmo")]
public class MonsterDetectionRangeGizmo : MonoBehaviour
{
    [Header("检测范围")]
    [Tooltip("普通检测范围（黄色）")]
    public float detectionRange = 20f;

    [Tooltip("战斗检测范围（红色）")]
    public float attackDetectionRange = 30f;

    [Header("脱战范围")]
    [Tooltip("脱战范围（蓝色）")]
    public float maxChaseDistance = 40f;

    [Tooltip("脱战中心（留 zero 则使用自身位置）")]
    public Vector3 patrolCenter = Vector3.zero;

    [Header("显示选项")]
    [Tooltip("是否填充圆盘（否则只画线框）")]
    public bool fillDisc = true;

    [Tooltip("是否显示标注文字")]
    public bool showLabels = true;

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        DrawRanges();
    }

    private void DrawRanges()
    {
        Vector3 center = transform.position;
        Vector3 chaseCenter = patrolCenter != Vector3.zero ? patrolCenter : center;

        DrawCircle(center, detectionRange, new Color(1f, 1f, 0f, 0.9f), new Color(1f, 1f, 0f, 0.12f));
        DrawCircle(center, attackDetectionRange, new Color(1f, 0.2f, 0.2f, 0.9f), new Color(1f, 0.2f, 0.2f, 0.07f));

        // 脱战范围只画线框
        Handles.color = new Color(0.2f, 0.4f, 1f, 0.9f);
        Handles.DrawWireDisc(chaseCenter, Vector3.up, maxChaseDistance);

        if (showLabels)
        {
            DrawLabel(center + Vector3.forward * detectionRange, $"  检测 {detectionRange:F1}m", Color.yellow);
            DrawLabel(center + Vector3.forward * attackDetectionRange, $"  战斗检测 {attackDetectionRange:F1}m", new Color(1f, 0.4f, 0.4f));
            DrawLabel(chaseCenter + Vector3.forward * maxChaseDistance, $"  脱战 {maxChaseDistance:F1}m", new Color(0.4f, 0.6f, 1f));
        }
    }

    private void DrawCircle(Vector3 center, float radius, Color lineColor, Color fillColor)
    {
        if (fillDisc)
        {
            Handles.color = fillColor;
            Handles.DrawSolidDisc(center, Vector3.up, radius);
        }
        Handles.color = lineColor;
        Handles.DrawWireDisc(center, Vector3.up, radius);
    }

    private static readonly GUIStyle _labelStyle = new GUIStyle();
    private void DrawLabel(Vector3 pos, string text, Color color)
    {
        _labelStyle.fontSize = 11;
        _labelStyle.normal.textColor = color;
        Handles.Label(pos, text, _labelStyle);
    }
#endif
}
