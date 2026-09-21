using NUnit.Framework;
using UnityEngine;

/// <summary>
/// status.json (python/src/unirobolab/status.py が書く) と、GUI が読む型の対応を固定する。
/// 片方だけ直すと ③ の内訳グラフと学習器の表示が黙って空になるので、形をここで押さえる。
/// </summary>
public class TrainStatusJsonTests
{
    const string k_Sample = @"{
      ""phase"": ""training"",
      ""success_rate"": 0.47,
      ""terms"": [
        {""key"": ""reached"", ""ja"": ""条件を満たせた回数"", ""en"": ""condition met"", ""penalty"": false,
         ""mean"": 34.75, ""has_prev"": true, ""prev"": 45.08, ""share"": 0.8068},
        {""key"": ""action_rate"", ""ja"": ""動きの荒さ"", ""en"": ""jerky commands"", ""penalty"": true,
         ""mean"": -0.95, ""has_prev"": false, ""prev"": -0.95, ""share"": 0.02}
      ],
      ""learner"": {""policy_std"": 0.211, ""explained_variance"": 0.06, ""approx_kl"": 0.0118,
                    ""clip_fraction"": 0.05, ""value_loss"": 2.8, ""updates"": 500},
      ""hints"": [{""key"": ""no_success"", ""ja"": ""成功が出ていません"", ""en"": ""no successes yet""}]
    }";

    [Test]
    public void ParsesTermsLearnerAndHints()
    {
        TrainStatusJson st = JsonUtility.FromJson<TrainStatusJson>(k_Sample);
        Assert.AreEqual("training", st.phase);
        Assert.AreEqual(2, st.terms.Length);
        Assert.AreEqual("reached", st.terms[0].key);
        Assert.IsFalse(st.terms[0].penalty);
        Assert.AreEqual(34.75f, st.terms[0].mean, 1e-4f);
        Assert.AreEqual(" ↓", st.terms[0].Arrow);              // 前の窓より減っている
        Assert.AreEqual("", st.terms[1].Arrow);                // 比較できる窓がまだ無い
        Assert.AreEqual(0.211f, st.learner.policy_std, 1e-4f);
        Assert.AreEqual(500, st.learner.updates);
        Assert.AreEqual(1, st.hints.Length);
    }

    [Test]
    public void MissingSectionsAreSafe()
    {
        TrainStatusJson st = JsonUtility.FromJson<TrainStatusJson>(@"{""phase"": ""done""}");
        Assert.AreEqual("done", st.phase);
        Assert.IsTrue(st.terms == null || st.terms.Length == 0);
        Assert.IsTrue(st.learner == null || st.learner.updates < 0);   // 「まだ学習していない」が分かる
    }
}
