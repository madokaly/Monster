namespace Game.Entities
{
    /// <summary>
    /// Monster Model 动画领域规则：AnimId 的<b>兵底层裁决</b>。
    /// <para>
    /// AnimId 按「层」划分所有权，不是由谁最后写谁赢：
    /// </para>
    /// <list type="bullet">
    /// <item>高优先层（死亡 / 受控 / 施法）——由各自领域在<b>进入时刻</b>写入（含随机取一的表现选择），
    /// 由对应 TickTimer 保护其有效期。本裁决器在这些层生效期间<b>让路</b>（不写，也不读）。</item>
    /// <item>兵底 locomotion 层——由本裁决器持有，每 tick 从状态事实投影。
    /// 高优先层全部退出时<b>自动接管</b>，所以任何领域都<b>不需要</b>在退出时补写回 Idle。</item>
    /// </list>
    /// <para>
    /// 纪律：高优先层的进入写入<b>必须先写 Timer 再写 AnimId</b>——两者之间保护尚未生效，
    /// 顺序颠倒等于把正确性押在"没人重排这两行"上。
    /// </para>
    /// </summary>
    public partial class MonsterModel
    {
        /// <summary> 判定"在移动"的速度下限（m/s）——低于此值视为站定 </summary>
        private const float MOVING_SPEED_THRESHOLD = 0.1f;

        #region Private Methods

        /// <summary>
        /// 动画裁决（FUN，权威端；由主文件 FixedUpdateNetwork 在计时器结算之后调用）。
        /// </summary>
        private void TickAnim()
        {
            // 高优先层占用中 → 让路。
            // 注意这里只是"不写"，不读 AnimId——裁决器与高优先层之间没有自反馈。
            if (ActState is MonsterActState.Dead
                or MonsterActState.Controlled
                or MonsterActState.Casting)
            {
                return;
            }

            SetAnimId(ResolveLocomotionAnim());
        }

        /// <summary>
        /// locomotion 动画投影（纯函数）。
        /// 两个输入正交：<b>"在不在动"取物理事实</b>（实际速度），<b>"走还是跑"取 AI 意图</b>（步态）。
        /// 不用速度阈值区分 Walk / Run——巡逻与追击速度仅差 1 m/s，
        /// A* 的加减速与转弯降速会让速度在阈值附近抖动，导致动画闪烁。
        /// </summary>
        private int ResolveLocomotionAnim()
        {
            if (MoveSpeedFact < MOVING_SPEED_THRESHOLD) return IdleAnim;

            return MoveCommand.Gait == MonsterGait.Walk ? WalkAnim : RunAnim;
        }

        #endregion
    }
}
