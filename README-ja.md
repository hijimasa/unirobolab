# UniRoboLab
[English](README.md) | 日本語

UniRoboLab は、Unity 上でロボットのポリシーを学習し、それを ROS 2 のロボットに
載せるまでを GUI から通すためのツールチェーンです。
**学習 → ROS 2 パッケージ生成 → sim2sim 検証 → デプロイ** を一続きに扱います。

大規模並列物理シミュレータの競合ではありません。それは Isaac Lab、mjlab、
Genesis が既に担っています。UniRoboLab が対象にするのはそれらが利用者に
丸投げしている後工程で、学習済みポリシーを「学習環境と観測・行動の配線が
一致していることが保証された ROS 2 ノード」に変換し、実機に触れる前に
シミュレーションで合否を出すことです。

## 提供するもの

- GUI・シミュレータ・sim2sim 検証を一つにまとめた Unity バイナリ。
  ONNX 推論は Unity の Inference Engine で完結し、Python は不要。
- バイナリに同梱され初回利用時に展開される学習バックエンド。NVIDIA 必須ではなく、
  GPU に応じて torch のホイールを選ぶ。
- 生成される ROS 2 パッケージ(C++ ノード + ONNX Runtime)。関節順・単位・スケール・
  制御周期は学習環境と共有する *ポリシー契約* から生成される。

## インストール(Python 側)

```bash
scripts/setup_python.sh              # uv で .venv を作り runtime extras を入れる (gen, import, live)
scripts/setup_python.sh --training   # + stable-baselines3 と CPU 版 torch (`unirobolab train`)
```

uv が無ければ入れる。シミュレータの `unirobolab.policy_runner_command` に書くインタプリタのパスを表示する。
シミュレータ相手の学習と sim2sim は `scripts/sim2sim_container.sh` のコンテナ(ROS 2 Jazzy)で行う。
Linux のみ確認済み。

## クイックスタート(servo_demo、配線確認ポリシー)

```bash
cd python && uv venv .venv && uv pip install -e ".[dev]" && cd ..
python/.venv/bin/unirobolab make-test-policy contract/examples/servo_demo.json
python/.venv/bin/unirobolab gen contract/examples/servo_demo.json --out generated --overwrite
scripts/sim2sim_container.sh start          # ROS 2 Jazzy + シミュレータのコンテナ(../Unity_ROS2_sample が要る)
scripts/sim2sim_container.sh build          # 派生イメージ: onnxruntime, torch-cpu, stable-baselines3 入り
```

コンテナ内の立ち上げ・ビルド・`unirobolab sim2sim` の手順は
[docs/architecture.md](docs/architecture.md) の 7 章を参照。実行の最後に PASS/FAIL の表と
`generated/sim2sim_out/report.json` が出る。学習(`unirobolab train`)は 8 章。

## 状態

M1 完了、M2 の第一段階完了: シミュレータ内で PPO 学習したポリシーを ONNX に書き出し、
ROS 2 パッケージ化して servo_demo の sim2sim に合格(ideal_joint 誤差 1 センチラジアン未満)。
学習は ROS 2 を通さない。本体の学習サーバ(`learning-server` ブランチ)が 1 シーンの K 体を
1 往復でまとめてステップし、servo_demo で 16 体並列 1,850 env steps/s(PPO 400k ステップが 4 分弱、整定誤差 0.006 rad、sim2sim PASS)。ROS 2 は配備と sim2sim の経路で、関節指令トピック直結と
ros2_control 経由(生成器がコントローラ設定も出す)の両方を確認済み。基体の観測(速度・重力方向・基体座標系の目標)も同じ経路で扱え、差動二輪の目標到達を 16 体並列で
学習した方策が、姿勢を ground_truth トピックから取る ROS 2 経路の sim2sim に合格している。学習は成功率の移動窓で早期終了し(diffbot は 43 分 → 3 分未満)、ロボットのスポーンも学習サーバ経由で
ROS 2 なしに行える。複数シミュレータプロセスのプールも使えるが、1 プロセスで大半のコアを使うため
このマシンでは約 2 倍止まり。Unity 6 に GPU 物理が無い件は
[docs/unity6-gpu-physics-survey.md](docs/unity6-gpu-physics-survey.md)。外部で学習した方策の取り込みは `unirobolab import-isaaclab`(Isaac Lab の `env.yaml` から契約を生成。
要素ごとの scale/offset、Twist 指令、IMU・オドメトリ入力、cmd_vel 出力を契約で表せる)。
sim2sim のシナリオは外乱注入とランダム目標に対応。GUI の最初の部品として、本体の Policy パネルから契約 + ONNX をスポーン済みロボットで ROS なしに
動かせる(`unirobolab live` を起動して学習サーバ経由で回す)。タブ式の UniRoboLab パネルで、契約の編集と検証、学習の開始・停止と学習曲線、sim2sim の結果表示が
できる。見た目の整備はこれから。
設計・マイルストーン・結果は [docs/architecture.md](docs/architecture.md) を参照。

## 構成

| パス | 役割 |
|---|---|
| `unity/UniRoboLab/` | Unity プロジェクト(GUI・シミュレータ・sim2sim)。シミュレータ本体は [Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator) を UPM の git パッケージとして参照 |
| `contract/` | ポリシー契約スキーマ。観測・行動・周期の単一の情報源 |
| `python/` | Python パッケージ `unirobolab`: `gen`(ROS 2 パッケージ生成)、`make-test-policy`、`sim2sim` 評価器、`train`(未実装) |
| `scripts/` | sim2sim 用のコンテナ起動・シミュレータ立ち上げ補助 |
| `docs/` | 設計メモ |

## ライセンス

Apache-2.0。UniRoboLab は個人の独立したプロジェクトであり、Unity Technologies
とは無関係で、その承認を受けたものでもありません。
