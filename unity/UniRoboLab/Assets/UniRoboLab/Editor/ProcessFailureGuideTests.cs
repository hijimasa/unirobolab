using NUnit.Framework;

public class ProcessFailureGuideTests
{
    [Test]
    public void TrainingTimeoutShowsCausePortTaskStepAndRetry()
    {
        var result = ProcessFailureGuide.ForTraining("TimeoutError: timed out", 1, 10100, true);

        Assert.That(result.FailureKind, Is.EqualTo(ProcessFailureGuide.Kind.Communication));
        Assert.That(result.Message, Does.Contain("学習サーバ応答なし"));
        Assert.That(result.Message, Does.Contain("1つだけ"));
        Assert.That(result.Message, Does.Contain("10100"));
        Assert.That(result.Message, Does.Contain("②"));
        Assert.That(result.Message, Does.Contain("再試行"));
        Assert.That(result.Message, Does.Contain("train.log"));
    }

    [Test]
    public void RunnerConnectionFailureShowsPlayerPortAndRunAgain()
    {
        var result = ProcessFailureGuide.ForRunner("ConnectionRefusedError: connection refused", 1, 10123, true);

        Assert.That(result.FailureKind, Is.EqualTo(ProcessFailureGuide.Kind.Communication));
        Assert.That(result.Message, Does.Contain("学習サーバ応答なし"));
        Assert.That(result.Message, Does.Contain("プレイヤー"));
        Assert.That(result.Message, Does.Contain("1つだけ"));
        Assert.That(result.Message, Does.Contain("10123"));
        Assert.That(result.Message, Does.Contain("動かす"));
        Assert.That(result.Message, Does.Contain("詳細"));
    }

    [TestCase("OSError: [Errno 98] Address already in use", ProcessFailureGuide.Kind.PortConflict)]
    [TestCase("ModuleNotFoundError: No module named 'onnxruntime'", ProcessFailureGuide.Kind.PythonEnvironment)]
    [TestCase("FileNotFoundError: policy.onnx", ProcessFailureGuide.Kind.MissingArtifact)]
    [TestCase("ValueError: observation shape mismatch", ProcessFailureGuide.Kind.Configuration)]
    [TestCase("an error without a known signature", ProcessFailureGuide.Kind.Unknown)]
    public void ClassifiesCommonFailures(string log, ProcessFailureGuide.Kind expected)
    {
        Assert.That(ProcessFailureGuide.Classify(log), Is.EqualTo(expected));
    }

    [Test]
    public void UnknownFailureStillGivesConcreteNextStepsInEnglish()
    {
        var result = ProcessFailureGuide.ForRunner("opaque failure", 7, 10100, false);

        Assert.That(result.Message, Does.Contain("exit 7"));
        Assert.That(result.Message, Does.Contain("player/port"));
        Assert.That(result.Message, Does.Contain("retry"));
        Assert.That(result.Message, Does.Contain("step 2"));
        Assert.That(result.Message, Does.Contain("Details"));
    }

    [Test]
    public void TaskReviewButtonIsNotSuggestedForEnvironmentOrPortFailures()
    {
        Assert.That(ProcessFailureGuide.ForTraining("Address already in use", 1, 10100, true).SuggestTaskReview, Is.False);
        Assert.That(ProcessFailureGuide.ForTraining("ModuleNotFoundError", 1, 10100, true).SuggestTaskReview, Is.False);
        Assert.That(ProcessFailureGuide.ForTraining("TimeoutError", 1, 10100, true).SuggestTaskReview, Is.True);
    }
}
