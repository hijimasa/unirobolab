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

## 状態

雛形。設計とマイルストーンは [docs/architecture.md](docs/architecture.md) を参照。

## 構成

| パス | 役割 |
|---|---|
| `unity/UniRoboLab/` | Unity プロジェクト(GUI・シミュレータ・sim2sim)。シミュレータ本体は [Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator) を UPM の git パッケージとして参照 |
| `contract/` | ポリシー契約スキーマ。観測・行動・周期の単一の情報源 |
| `trainer/` | Python 学習バックエンド(uv 管理、バイナリに同梱) |
| `templates/ros2_policy_node/` | 生成する ROS 2 パッケージの雛形 |
| `docs/` | 設計メモ |

## ライセンス

Apache-2.0。UniRoboLab は個人の独立したプロジェクトであり、Unity Technologies
とは無関係で、その承認を受けたものでもありません。
