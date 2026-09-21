using UnityEngine;
using System;

/// <summary>
/// 学習中の status.json (python/src/unirobolab/status.py が書く) のうち、GUI が構造として使う部分。
/// 文言 (項目名・次の一手) は Python 側が持つので、ここでは受け取って表示するだけ。
/// </summary>
[Serializable]
public class TrainTerm
{
    public string key;
    public string ja;
    public string en;
    public bool penalty;        // 罰 (負の重み) か
    public float mean;          // 直近の窓での 1 試行あたりの平均
    public float prev;          // その前の窓での平均 (has_prev が false なら mean と同じ)
    public bool has_prev;
    public float share;         // 効いている割合 (|mean| の比)

    public string Name => Ui.Japanese ? ja : en;
    public string Arrow => !has_prev || Mathf.Approximately(mean, prev) ? "" : (mean > prev ? " ↑" : " ↓");
}

[Serializable]
public class TrainLearner
{
    public float policy_std = -1f;            // 方策のばらつき (探索の広さ)
    public float explained_variance = -999f;  // 価値関数の当たり具合 (1 に近いほど良い)
    public float approx_kl = -1f;             // 1 回の更新の大きさ
    public float clip_fraction = -1f;         // 打ち切られた割合
    public float value_loss = -1f;
    public int updates = -1;
}

[Serializable] public class TrainHint { public string key; public string ja; public string en; }

[Serializable]
public class TrainStatusJson
{
    public string phase;
    public TrainTerm[] terms;
    public TrainLearner learner;
    public TrainHint[] hints;
}
