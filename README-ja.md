# UniRoboLab
[English](README.md) | 日本語

UniRoboLab は、Unity のシミュレータでロボットの方策を学習し、ROS 2 のロボットへ載せるまでを
GUI から通すためのツールチェーンです。1 つの窓で 6 段階を進めます:

**① ロボット** (URDF を読む) → **② タスク** (開始条件と成功条件を 3D で決める) → **③ 学習**
(シミュレータの学習サーバに直結した PPO、ROS 2 不要) → **④ 試す** (方策を 3D で動かす) →
**⑤ チェック** (実機と同じ ROS 2 経路で動かして合否を出し、非常停止も試す) → **⑥ 実機へ**
(生成された ROS 2 パッケージと平易な手順書)。

大規模並列物理シミュレータの競合ではありません。それは Isaac Lab、mjlab、Genesis が既に担っています。
UniRoboLab が対象にするのはそれらが利用者に丸投げしている後工程で、方策を「学習環境と観測・行動の配線が
一致していることが保証された ROS 2 ノード」にし、実機に触れる前にシミュレーションで合否を出すことです。
単一の情報源は *ポリシー契約* (`contract/`) で、学習環境と生成ノードの両方をそこから作ります。

## 入手

- **リリース** (Linux x86_64、Windows x64): プレイヤー・Python パッケージ・チュートリアル入りの zip。
  セットアップは zip 内の README ([release/README-ja.md](release/README-ja.md)) を参照。
- **自分でビルドする**: 下記。

## ソースからビルドする

必要なもの:

- Unity **6000.3.21f1** (Unity Hub) と *Linux Build Support (Mono)*。Windows 版を作るなら
  *Windows Build Support (Mono)* も (Linux ホストから Windows 版をビルドできます)。
- 初回ビルド時のネットワーク接続: シミュレータ本体などの Unity パッケージは
  `unity/UniRoboLab/Packages/manifest.json` にコミット固定の UPM git 依存として書いてあります
  ([Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator) の
  `Packages/SimulationCore` ほか、ROS-TCP-Connector・URDF-Importer・UnitySensors の hijimasa フォーク)。
- Python 3.10 以上はビルドには不要で、実行時に `scripts/setup_python.sh` で入れます。

```bash
git clone https://github.com/hijimasa/unirobolab.git && cd unirobolab
scripts/build_player.sh                                        # -> generated/player/UniRoboLab.x86_64
scripts/build_player.sh generated/player_win/UniRoboLab.exe windows   # Windows 版 (任意)
scripts/setup_python.sh --training                             # 学習バックエンド入りの .venv
scripts/unirobolab_gui.sh ~/unirobolab_projects/first          # GUI を起動
```

`build_player.sh` は `~/Unity/Hub/Editor/6000.*` のエディタを使います (`UNITY` で指定可)。シーンはビルド時に
生成する (`Assets/UniRoboLab/Editor/LabSceneBuilder.cs`) ので、通常のビルドでエディタを開く必要はありません。
ビルドのログは `generated/log/build_player.log` です。

リリース用の zip (プレイヤー + `python/` + `scripts/` + `contract/` + チュートリアルを、プレイヤーが実行時に
期待する配置で): 両方のプレイヤーをビルドしたあと `scripts/make_release.sh`。

⑤ には ROS 2 が要ります。ホストに無ければ `scripts/sim2sim_container.sh start` が自己完結の ROS 2 Jazzy
コンテナを使います (`docker/Dockerfile`: ros-base に ROS-TCP エンドポイント、simulation_interfaces、
simulation_ros2_utils、topic_based_ros2_control をコミット固定のソースからビルド、約 1.3 GB)。イメージは
初回の start で作られます (10 分ほど、ネットワークが要ります)。⑤ の画面のボタンでも同じことができます。

テスト: `PYTHONPATH=python/src pytest python/tests` (シミュレータ不要)。

## チュートリアル

GUI で URDF から実機用パッケージまで通す手順 (画面付き): [docs/tutorial.md](docs/tutorial.md)。
サーボ (関節目標)、腕で物体を押すタスク、開始姿勢と物理のばらつきの設定を扱います。

## コマンドライン

GUI がすることはすべて `unirobolab` のサブコマンドです (`.venv/bin/unirobolab --help`): `robot-info`、
`task-preset`、`task-gen`、`train`、`train-status`、`live`、`eval`、`scenario-default`、`gen`、`sim2sim`、
`explain-report`、`deploy-guide`、`import-isaaclab` (Isaac Lab の `env.yaml` から契約を作る)、`make-test-policy`。

## 状態

この PC で端から端まで確認済み (詳細は [docs/architecture.md](docs/architecture.md)):

- 関節目標の方策 (サーボのデモ) と差動二輪の地点到達の方策を ①〜⑥ で通した (ros2_control 経由の配備も)。
- 平面腕での手先の目標と物体を押すタスク: 増分行動での学習、開始姿勢と物理の domain randomization、
  履歴窓の方策、物体の位置を実機のカメラの代わりに再配信する ⑤ のチェック。
- 学習の速度は CPU で、サーボ 16 体で約 1,800 env steps/s、物体付きの腕 8 体で約 280 env steps/s。

既知の制限: 条件は 1 タスクに 1 つ。把持は未対応 (押すのみ)。⑤ は Linux のみ。Windows 版は補助コマンドを
Git for Windows の `bash.exe` で動かす作りで、実機ではまだ試していません。学習済み方策の評価は
40 エピソードで ±10 % ほどばらつきます。

## 構成

| パス | 役割 |
|---|---|
| `unity/UniRoboLab/` | Unity プロジェクト: ウィザード GUI (`Assets/UniRoboLab/Scripts`)、シーン生成とビルド (`Assets/UniRoboLab/Editor`)。シミュレータ本体は Unity_ROS2_Robot_Simulator を UPM の git パッケージとして参照 |
| `contract/` | ポリシー契約のスキーマと例: 観測・行動・周期・安全上限・ROS トピック |
| `python/` | パッケージ `unirobolab`: タスク仕様 → 契約 + 学習設定、PPO 学習器、試運転、ROS 2 パッケージ生成、sim2sim 評価器、配備手順書 |
| `scripts/` | プレイヤーのビルド、GUI の起動、Python 環境 (bash / PowerShell)、ROS 2 コンテナ、⑤ の実行器、リリース zip の組み立て |
| `release/` | リリース zip に同梱する README |
| `docker/` | ROS 2 コンテナの派生イメージ (onnxruntime、torch-cpu、stable-baselines3) |
| `docs/` | 設計と結果 (`architecture.md`)、動線 (`ux-flow.md`)、チュートリアルと画面 |

## ライセンス

Apache-2.0。UniRoboLab は個人の独立したプロジェクトであり、Unity Technologies
とは無関係で、その承認を受けたものでもありません。
