using System;
using Framework;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 感知结果快照（权威端派生数据，不 [Networked]）。
    /// 由 MonsterPerceptionModule 节流重算写入，权威易主后重算覆盖。
    /// </summary>
    public struct MonsterPerceptionSnapshot : IEquatable<MonsterPerceptionSnapshot>
    {
        /// <summary> 当前有效范围内是否存在任意目标 </summary>
        public bool HasAnyTarget;

        /// <summary>
        /// 当前有效范围层级。
        /// 0: 无目标（脱战圈已空，硬脱战）；
        /// 1: 搜索圈内有目标；
        /// 2: 搜索圈空，追击圈内有目标；
        /// 3: 搜索 / 追击圈空，仅脱战圈内有目标（超出追击范围，AI 视作脱战）。
        /// </summary>
        public int RangeType;

        /// <summary> 最佳目标（仇恨优先级优先，同优先级取最近）</summary>
        public EntityId BestTargetId;

        /// <summary> 最佳目标的距离（无目标时为 -1）</summary>
        public float BestTargetDistance;

        /// <summary> 当前有效范围内的目标数量 </summary>
        public int TargetCount;

        public bool Equals(MonsterPerceptionSnapshot other)
        {
            return HasAnyTarget == other.HasAnyTarget
                   && RangeType == other.RangeType
                   && BestTargetId.Equals(other.BestTargetId)
                   && BestTargetDistance.Equals(other.BestTargetDistance)
                   && TargetCount == other.TargetCount;
        }

        public override bool Equals(object obj)
        {
            return obj is MonsterPerceptionSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(HasAnyTarget, RangeType, BestTargetId, BestTargetDistance, TargetCount);
        }

        public static bool operator ==(MonsterPerceptionSnapshot left, MonsterPerceptionSnapshot right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(MonsterPerceptionSnapshot left, MonsterPerceptionSnapshot right)
        {
            return !left.Equals(right);
        }
    }

    /// <summary>
    /// 移动步态（AI 意图，决定 locomotion 动画选 Walk 还是 Run）。
    /// "在不在动"由真实速度事实决定（见 MonsterModel.MoveSpeedFact），本枚举只表达"动起来时是走还是跑"。
    /// </summary>
    public enum MonsterGait : byte
    {
        Walk = 0,
        Run = 1,
    }

    /// <summary>
    /// 移动指令（权威端派生数据，不 [Networked]）。
    /// 由 MonsterAIModule 每 tick 重申写入（持续声明的意图，非一次性事件），MonsterMoveModule 监听执行。
    /// 施法期间所有权让给技能步骤（步骤自行写 IsStopped 停 Follower 后手动位移）。
    /// </summary>
    public struct MonsterMoveCommand : IEquatable<MonsterMoveCommand>
    {
        /// <summary> 是否原地停止 </summary>
        public bool IsStopped;

        /// <summary> 寻路目的地（IsStopped 时忽略）</summary>
        public Vector3 Destination;

        /// <summary> 移动速度 </summary>
        public float MoveSpeed;

        /// <summary> 移动步态（locomotion 动画选 Walk / Run 的意图来源）</summary>
        public MonsterGait Gait;

        /// <summary> 到达判定距离 </summary>
        public float StopDistance;

        public bool Equals(MonsterMoveCommand other)
        {
            return IsStopped == other.IsStopped
                   && Destination.Equals(other.Destination)
                   && MoveSpeed.Equals(other.MoveSpeed)
                   && Gait == other.Gait
                   && StopDistance.Equals(other.StopDistance);
        }

        public override bool Equals(object obj)
        {
            return obj is MonsterMoveCommand other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(IsStopped, Destination, MoveSpeed, Gait, StopDistance);
        }

        public static bool operator ==(MonsterMoveCommand left, MonsterMoveCommand right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(MonsterMoveCommand left, MonsterMoveCommand right)
        {
            return !left.Equals(right);
        }
    }
}
