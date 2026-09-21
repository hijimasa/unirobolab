using System;
using System.IO;
using NUnit.Framework;

public class PhaseDeployTests
{
    string m_Dir;
    Project m_Project;

    [SetUp]
    public void SetUp()
    {
        m_Dir = Path.Combine(Path.GetTempPath(), "unirobolab-deploy-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_Dir);
        File.WriteAllText(Path.Combine(m_Dir, "task.json"), "task-v1");
        File.WriteAllText(Path.Combine(m_Dir, "contract.json"), "contract-v1");
        Directory.CreateDirectory(Path.Combine(m_Dir, "run"));
        File.WriteAllText(Path.Combine(m_Dir, "run", "policy.onnx"), "policy-v1");

        m_Project = Project.Open(m_Dir);
        m_Project.D.task = "task.json";
        m_Project.D.contract = "contract.json";
        m_Project.D.run_dir = "run";
        m_Project.Save();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(m_Dir)) Directory.Delete(m_Dir, true);
    }

    void WriteReport(bool pass)
    {
        File.WriteAllText(Path.Combine(m_Dir, "report.json"), "{\"pass\":" + (pass ? "true" : "false") + "}");
        m_Project.D.report = "report.json";
        m_Project.Save();
    }

    void StampPassingReport()
    {
        WriteReport(true);
        m_Project.Stamp("report");
    }

    [Test]
    public void CheckState_RequiresReport()
    {
        Assert.That(PhaseDeploy.CheckState(m_Project), Is.EqualTo(PhaseDeploy.CheckGateState.Missing));
    }

    [Test]
    public void CheckState_RejectsPassingReportWithoutPassStamp()
    {
        WriteReport(true);
        Assert.That(PhaseDeploy.CheckState(m_Project), Is.EqualTo(PhaseDeploy.CheckGateState.Unverified));
    }

    [Test]
    public void CheckState_AcceptsCurrentPassingStampedReport()
    {
        StampPassingReport();
        Assert.That(PhaseDeploy.CheckState(m_Project), Is.EqualTo(PhaseDeploy.CheckGateState.Ready));
    }

    [Test]
    public void CheckState_RejectsLatestFailureEvenWithOldPassStamp()
    {
        StampPassingReport();
        WriteReport(false);
        Assert.That(PhaseDeploy.CheckState(m_Project), Is.EqualTo(PhaseDeploy.CheckGateState.NotPassed));
    }

    [TestCase("task")]
    [TestCase("contract")]
    [TestCase("policy")]
    [TestCase("report")]
    public void CheckState_RejectsChangesAfterPass(string changed)
    {
        StampPassingReport();
        switch (changed)
        {
            case "task": File.WriteAllText(Path.Combine(m_Dir, "task.json"), "task-v2"); break;
            case "contract": File.WriteAllText(Path.Combine(m_Dir, "contract.json"), "contract-v2"); break;
            case "policy": File.WriteAllText(Path.Combine(m_Dir, "run", "policy.onnx"), "policy-v2"); break;
            case "report": File.WriteAllText(Path.Combine(m_Dir, "report.json"), "{\"pass\":true,\"new\":true}"); break;
        }
        Assert.That(PhaseDeploy.CheckState(m_Project), Is.EqualTo(PhaseDeploy.CheckGateState.Stale));
    }
}
