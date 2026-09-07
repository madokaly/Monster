using System.Collections.Generic;
using Framework;
using UnityEngine;

namespace Game.Components
{
    /// <summary> 召唤物判定窗口形状 </summary>
    public enum MonsterSummonDamageShape
    {
        Disc,     // 全圆
        Annulus,  // 环带
    }

    /// <summary> 判定窗口相位（预演绘制配色用） </summary>
    public enum MonsterSummonWindowPhase
    {
        Upcoming,  // 尚未开始
        Active,    // 判定进行中（瞬时窗口在命中时刻邻域内）
        Finished,  // 已结束（子类一般不再返回）
    }

    /// <summary>
    /// 召唤物判定几何窗口（声明式，几何与相位由子类按 localTime 求值；
    /// 圆心统一为召唤物生成位，由预演适配器计算）。
    /// </summary>
    public struct MonsterSummonDamageWindow
    {
        public MonsterSummonDamageShape Shape;
        public MonsterSummonWindowPhase Phase;

        /// <summary> 内半径（Disc 恒 0） </summary>
        public float InnerRadius;

        /// <summary> 外半径 </summary>
        public float OuterRadius;

        /// <summary> 跳躲高度过滤阈值（<= 0 不过滤） </summary>
        public float MaxAttackHeight;

        /// <summary> 伤害值（预演标注用） </summary>
        public int Damage;

        /// <summary> 预演标注 </summary>
        public string Label;
    }

    /// <summary>
    /// 召唤物行为基类（本地简易子实体的行为主体，§0.1.3 本地变体）：
    /// 由 MonsterSummonStep 在各端本地实例化，只发不收（伤害经 Msger 受击方本地结算，§1.7）、
    /// 生命周期自管（时间轴播完自毁；StopDamage 停伤不停表现；父实体销毁时由步骤统一回收）。
    /// 子类 = 单个召唤技能的全部逻辑（时间轴 / 表现 / 判定），数值时序内联序列化字段。
    /// 预演契约（§18.6）：SampleTimeline 为时间的纯函数（可正反拖动、禁物理禁伤害禁总线），
    /// GetDamageWindows 声明式输出当前时刻判定几何，Editor 通用适配器统一驱动与绘制——
    /// 新子类零预演代码。
    /// </summary>
    public abstract class MonsterSummonBehaviour : MonoBehaviour
    {
        /// <summary> 召唤者实体身份（伤害归因，DamageData.AttackerId；纯值注入，不持怪物引用） </summary>
        protected EntityId AttackerId { get; private set; }

        /// <summary> 召唤物自身时间轴（秒，生成起算；仅运行时推进） </summary>
        protected float Elapsed { get; private set; }

        /// <summary> 是否已初始化（未初始化 = 预演克隆，时间轴不推进） </summary>
        protected bool Initialized { get; private set; }

        /// <summary> 伤害已停止（步骤 Exit / 打断；时间轴与表现不受影响） </summary>
        protected bool DamageStopped => _damageStopped;

        private bool _damageStopped;

        #region Lifecycle

        /// <summary>
        /// 初始化（由召唤步骤在实例化后调用，全端各一次）。
        /// </summary>
        public void Initialize(EntityId attackerId)
        {
            AttackerId = attackerId;
            Elapsed = 0f;
            Initialized = true;

            OnInitialize();
        }

        /// <summary>
        /// 停止后续伤害判定（步骤 Exit：窗口到期 / 打断 / 易主重置；表现时间轴继续自然播完）。
        /// </summary>
        public void StopDamage()
        {
            if (!Initialized || _damageStopped) return;

            _damageStopped = true;
            OnDamageStopped();
        }

        private void Update()
        {
            if (!Initialized) return;

            Elapsed += Time.deltaTime;
            OnTimelineTick(Elapsed);
        }

        protected virtual void OnDestroy()
        {
            Initialized = false;
        }

        #endregion

        #region Overridables

        /// <summary> 初始化钩子（重置中间态 / 初始化表现） </summary>
        protected abstract void OnInitialize();

        /// <summary> 时间轴逐帧驱动（表现推进与判定；判定前自行检查 DamageStopped） </summary>
        protected abstract void OnTimelineTick(float elapsed);

        /// <summary> 停伤钩子（停轮询 / 关触发等；默认空） </summary>
        protected virtual void OnDamageStopped() { }

        #endregion

        #region Preview Contract（Editor 适配器驱动；运行时不调用）

        /// <summary> 召唤物完整时间轴时长（预演窗口派生） </summary>
        public abstract float TimelineDuration { get; }

        /// <summary>
        /// 表现采样（纯时间函数，可正反拖动；预演专用——不推进 Elapsed、不判定、不做任何物理查询）。
        /// </summary>
        public abstract void SampleTimeline(float localTime);

        /// <summary>
        /// 当前时刻的判定几何窗口（预演绘制；瞬时窗口在命中时刻邻域内返回 Active）。
        /// </summary>
        public abstract void GetDamageWindows(float localTime, List<MonsterSummonDamageWindow> results);

        #endregion

        #region Helpers

        /// <summary>
        /// 激活并重启视觉（运行时路径：SetActive + 粒子 Clear/Play，重复触发干净生效）。
        /// </summary>
        protected static void PlayVfx(GameObject vfx)
        {
            if (vfx == null) return;

            vfx.SetActive(true);
            var particles = vfx.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                if (particles[i] == null) continue;
                particles[i].Clear(true);
                particles[i].Play(true);
            }
        }

        /// <summary>
        /// 表现采样到指定年龄（预演路径：粒子 Stop/Simulate/Pause，确定性支持时间轴拖动）。
        /// </summary>
        protected static void SimulateVfx(GameObject vfx, float age)
        {
            if (vfx == null) return;

            var particles = vfx.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                var particleSystem = particles[i];
                if (particleSystem == null || !particleSystem.gameObject.activeInHierarchy) continue;

                particleSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                particleSystem.Simulate(Mathf.Max(0f, age), false, true, false);
                particleSystem.Pause(false);
            }
        }

        #endregion
    }
}
