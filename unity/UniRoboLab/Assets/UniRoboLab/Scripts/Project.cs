using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

/// <summary>
/// プロジェクト = 1 フォルダ (URDF、タスク仕様、契約、学習設定、runs/、sim2sim_out/、deploy/)。
/// 各成果物には「元になった入力のハッシュ」を刻み、上流が変わったら下流を「要やり直し」にする。
/// </summary>
[Serializable]
public class ProjectData
{
    public string urdf = "";            // プロジェクトからの相対パス
    public string name = "";
    public string ns = "";
    public string command_mode = "joint_state_topic";
    public string controller = "";
    public string estop_topic = "";
    public string task = "task.json";
    public string contract = "";        // 相対
    public string train = "";
    public string run_dir = "";         // 今の方策
    public string report = "";
    public string deploy_dir = "";
    public int phase_reached = 0;
    public List<string> stamp_keys = new List<string>();
    public List<string> stamp_values = new List<string>();
}

public class Project
{
    public const string FileName = "unirobolab.project.json";
    public string Dir { get; }
    public ProjectData D { get; private set; }

    Project(string dir, ProjectData d) { Dir = dir; D = d; }

    public static Project Open(string dir)
    {
        string path = Path.Combine(dir, FileName);
        ProjectData d = File.Exists(path) ? JsonUtility.FromJson<ProjectData>(File.ReadAllText(path)) : new ProjectData();
        if (d == null) d = new ProjectData();
        var p = new Project(dir, d);
        if (!File.Exists(path)) { Directory.CreateDirectory(dir); p.Save(); }
        RememberRecent(dir);
        return p;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, FileName), JsonUtility.ToJson(D, true));
    }

    public string Abs(string rel) => string.IsNullOrEmpty(rel) ? "" : (Path.IsPathRooted(rel) ? rel : Path.GetFullPath(Path.Combine(Dir, rel)));
    public string Rel(string abs)
    {
        if (string.IsNullOrEmpty(abs)) return "";
        string full = Path.GetFullPath(abs), dir = Path.GetFullPath(Dir).TrimEnd('/') + "/";
        return full.StartsWith(dir) ? full.Substring(dir.Length) : full;
    }
    public bool Has(string rel) => !string.IsNullOrEmpty(rel) && (File.Exists(Abs(rel)) || Directory.Exists(Abs(rel)));
    public string OnnxPath => string.IsNullOrEmpty(D.run_dir) ? "" : Path.Combine(Abs(D.run_dir), "policy.onnx");

    // ------------------------------------------------------------- stamps
    public static string HashFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "missing";
            using var sha = SHA1.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").Substring(0, 16);
        }
        catch (Exception) { return "err"; }
    }

    static string HashText(string s)
    {
        using var sha = SHA1.Create();
        return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s ?? ""))).Replace("-", "").Substring(0, 16);
    }

    /// <summary>成果物 key の入力 (今の状態) をまとめたハッシュ。</summary>
    public string InputsOf(string key)
    {
        switch (key)
        {
            case "contract": return HashFile(Abs(D.urdf)) + HashText(D.name + "|" + D.ns + "|" + D.command_mode + "|" + D.controller + "|" + D.estop_topic);
            case "train": return HashFile(Abs(D.task)) + HashFile(Abs(D.contract));
            case "run": return HashFile(Abs(D.train)) + HashFile(Abs(D.contract));
            case "report": return HashFile(Abs(D.contract)) + HashFile(OnnxPath);
            case "deploy": return HashFile(Abs(D.contract)) + HashFile(OnnxPath);
        }
        return "";
    }

    public void Stamp(string key)
    {
        int i = D.stamp_keys.IndexOf(key);
        string v = InputsOf(key);
        if (i < 0) { D.stamp_keys.Add(key); D.stamp_values.Add(v); } else D.stamp_values[i] = v;
        Save();
    }

    /// <summary>成果物があるのに、その入力が刻んだときと違う → 要やり直し。</summary>
    public bool IsStale(string key)
    {
        if (!Exists(key)) return false;
        int i = D.stamp_keys.IndexOf(key);
        return i >= 0 && D.stamp_values[i] != InputsOf(key);
    }

    public bool Exists(string key)
    {
        switch (key)
        {
            case "contract": return Has(D.contract);
            case "train": return Has(D.train);
            case "run": return File.Exists(OnnxPath);
            case "report": return Has(D.report);
            case "deploy": return Has(D.deploy_dir);
        }
        return false;
    }

    // ------------------------------------------------------------- recent
    static string RecentFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "unirobolab", "recent.txt");

    static void RememberRecent(string dir)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(RecentFile)); File.WriteAllText(RecentFile, Path.GetFullPath(dir) + "\n"); } catch (Exception) { }
    }

    public static string LastDir()
    {
        try { if (File.Exists(RecentFile)) { string d = File.ReadAllText(RecentFile).Trim(); if (Directory.Exists(d)) return d; } } catch (Exception) { }
        return null;
    }

    public static string DefaultDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "unirobolab_projects", "project1");
}
