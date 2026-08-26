using Framework.Core;
using UnityEngine;

namespace Game.Entities
{
    /// <summary>
    /// 技能进入表现共享管线（§16.3）：链级（EnterEffects / EnterSounds）与步骤级（StepEffects / StepSounds）
    /// 各端本地随机播一个（两端可能不同，遵循 Melee 旧制 CastSoundPaths 先例）；空数组 = 跳过。
    /// 动画不在本类：权威端随机取一后经 Model.SetAnimId（[Networked] 状态事实）全端一致播放（调用方负责）。
    /// </summary>
    public static class MonsterSkillPresentation
    {
        /// <summary>
        /// 随机播一个特效（各端本地）：实例化 settings.Prefab 到挂点
        /// （AttachPoint → FallbackTransform → fallbackTransform），应用本地偏移 / 旋转 / 缩放，到时自毁。
        /// 数组空 / 元素空 = 跳过。
        /// </summary>
        public static void PlayRandomEffect(MonsterEffectSettings[] settingsArray, Transform fallbackTransform = null)
        {
            if (settingsArray is not { Length: > 0 }) return;

            var settings = settingsArray[Random.Range(0, settingsArray.Length)];
            if (settings == null) return;
            if (settings.Prefab == null) return;

            var attach = settings.AttachPoint != null
                ? settings.AttachPoint
                : settings.FallbackTransform != null
                    ? settings.FallbackTransform
                    : fallbackTransform;
            if (attach == null) return;

            var effectObj = Object.Instantiate(settings.Prefab, attach.position, attach.rotation, attach);
            effectObj.transform.localPosition = settings.Offset;
            effectObj.transform.localEulerAngles = settings.Rotation;
            effectObj.transform.localScale = settings.Scale;

            float lifetime = settings.Lifetime > 0f ? settings.Lifetime : 3f;
            Object.Destroy(effectObj, Mathf.Max(0.1f, lifetime));
        }

        /// <summary>
        /// 随机播一个音效（各端本地）：数组空 / 挂点空 / 路径空 = 跳过。
        /// </summary>
        public static void PlayRandomSound(string[] soundPaths, Transform attach)
        {
            if (soundPaths is not { Length: > 0 }) return;
            if (attach == null) return;

            string sound = soundPaths[Random.Range(0, soundPaths.Length)];
            if (string.IsNullOrEmpty(sound)) return;

            AudioMgr.Play(sound, attach);
        }
    }
}
