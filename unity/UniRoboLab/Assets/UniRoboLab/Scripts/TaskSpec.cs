using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>task.json (docs/ux-flow.md v2.1、python/src/unirobolab/taskspec.py と同じ形)。GUI が書く唯一の JSON。</summary>
[Serializable] public class SpecRobot { public string urdf = ""; public string name = ""; public string @namespace = ""; public string command_mode = "joint_state_topic"; public string controller = ""; public string estop_topic = ""; }
[Serializable] public class SpecBase { public float[] xy = { 0f, 0f }; public float[] yaw_deg = { 180f, 180f }; /* 既定: 正面がカメラを向く */ }
[Serializable] public class SpecStart { public string joints = "zero"; public SpecBase @base = new SpecBase(); }
[Serializable] public class SpecRegion { public string shape = "ring"; public float[] center = { 0f, 0f }; public float r_min = 1f; public float r_max = 2.5f; public float[] angle_deg = { -180f, 180f }; public float[] size = { 1f, 1f }; public float radius = 0.5f; }
[Serializable] public class SpecGoal { public string type = "joints_near"; public float[] range = new float[0]; public float tolerance = 0.05f; public SpecRegion region = new SpecRegion(); public bool stop_at_goal = false; }
[Serializable] public class SpecEpisode { public float time_s = 2f; public float hold_s = 0f; }
[Serializable] public class SpecTraining { public int n_envs = 8; public float success_target = 0.9f; }
[Serializable]
public class TaskSpec
{
    public int spec_version = 0;
    public SpecRobot robot = new SpecRobot();
    public SpecStart start = new SpecStart();
    public List<SpecGoal> goal = new List<SpecGoal>();
    public SpecEpisode episode = new SpecEpisode();
    public SpecTraining training = new SpecTraining();

    public static TaskSpec Load(string path)
    {
        try { if (File.Exists(path)) { var s = JsonUtility.FromJson<TaskSpec>(File.ReadAllText(path)); if (s != null) return s; } } catch (Exception e) { Debug.LogWarning("[TaskSpec] " + e.Message); }
        return new TaskSpec();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(this, true));
    }

    public SpecGoal Goal0 { get { if (goal.Count == 0) goal.Add(new SpecGoal()); return goal[0]; } }
}

/// <summary>unirobolab robot-info の出力。</summary>
[Serializable] public class RobotJoint { public string name; public string type; public string mode; public float lower; public float upper; public float velocity; public float effort; }
[Serializable] public class RobotInfo { public string name; public bool fixed_base = true; public List<RobotJoint> joints = new List<RobotJoint>(); public bool imu; public bool odom; }
