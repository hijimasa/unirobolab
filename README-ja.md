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
学習は本体の `step-and-observe` ブランチで作ったシミュレータに対して約 90 steps/s
(制御 1 周期 = サービス 1 往復、TCP コネクタの Nagle 無効化)。GUI とベクトル化学習はこれから。
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
