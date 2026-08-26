namespace Game.Components
{
    /// <summary>
    /// 怪物身份组件（只读身份，无行为）。
    /// 命中检测只读本组件解析 EntityId，即用即弃（§1.6）。
    /// </summary>
    public class MonsterTag : EntityTag { }
}
