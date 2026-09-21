# UniRoboLab

[English](README.md) | 日本語

UniRoboLab は、Unity のシミュレータでロボットの方策を学習し、ROS 2 のロボットへ載せるまでを
1 つの窓で 6 段階に通すツールです: **① ロボット → ② タスク → ③ 学習 → ④ 試す → ⑤ チェック → ⑥ 実機へ**。
利用者が用意するファイルは URDF だけです。そこから学習の設定が作られ、学習した方策を 3D で試し、
ROS 2 経由のシミュレーションで合否を出し、ROS 2 パッケージと実機向けの手順書を書き出します。
ただし PC には下の表のものが必要で、⑤ と ⑥ では実機の ROS 2 の情報 (名前空間、指令トピックまたは
コントローラ、非常停止トピック) を ① で入力します。

画面付きの手順は [docs/tutorial.md](docs/tutorial.md) にあります。

## 動作環境

| | Linux (x86_64) | Windows (x64) |
|---|---|---|
| ①〜④ (学習して試す) | Ubuntu 22.04 / 24.04、Vulkan か OpenGL が動く GPU | Windows 10 / 11、**Git for Windows** (補助コマンドを `bash.exe` で動かします)、PowerShell |
| ⑤ (ROS 2 経由のチェック) | この PC の ROS 2 Jazzy、**または** Docker (初回に 1.3 GB の ROS 2 イメージを作ります) | 未対応 (Linux の PC か、WSL 2 上で Linux 版を使ってください) |
| ⑥ (実機へ) | ロボット側の ROS 2 ワークスペース | 同じ |

Python はセットアップスクリプトが [uv](https://astral.sh/uv) でこのフォルダの中 (`.venv`) に入れます。
PC の他の環境には触れません。学習は CPU で動き、NVIDIA の GPU は要りません。

## セットアップ (一度だけ)

Linux:

```bash
unzip UniRoboLab-*-linux-x86_64.zip && cd UniRoboLab-*-linux-x86_64
scripts/setup_python.sh --training        # .venv を作ります (初回は数分)
./UniRoboLab.sh ~/unirobolab_projects/first    # 引数はプロジェクトのフォルダ (無ければ作られます)
```

Windows (PowerShell):

```powershell
Expand-Archive UniRoboLab-*-windows-x86_64.zip; cd UniRoboLab-*-windows-x86_64
powershell -ExecutionPolicy Bypass -File scripts\setup_python.ps1 -Training
.\UniRoboLab.cmd $HOME\unirobolab_projects\first
```

あとは [docs/tutorial.md](docs/tutorial.md) の ① から進めてください。窓の上に 6 段階が並び、
今の段階が青、済んだ段階が緑で示されます。下の状態行に「次に何をするか」と、ボタンが押せない理由が出ます。

Linux で ROS 2 が入っていない PC の ⑤: Docker を入れ (自分のユーザーで `docker` が使えること)、⑤ の画面の
「コンテナを起動」を押すか `scripts/sim2sim_container.sh start` を実行します。初回は `docker/Dockerfile` から
ROS 2 のイメージを作ります (10 分ほど、ネットワークが要ります)。2 回目からは数秒で立ち上がります。

## フォルダの中身

| パス | 役割 |
|---|---|
| `UniRoboLab.sh` / `UniRoboLab.cmd` | 既定の設定で GUI (`player/`) を起動します |
| `player/` | Unity のアプリ本体 (GUI、シミュレータ、ポート 10100 の学習サーバ) |
| `python/` | `unirobolab` パッケージ: タスクからの設定生成、学習、試運転、ROS 2 パッケージ生成、チェック |
| `scripts/` | `setup_python.sh` / `.ps1`、`sim2sim_container.sh` (⑤ 用の ROS 2 コンテナ)、`check_runner.sh` |
| `contract/` | 方策の契約 (方策が何を見て何を出すか) のスキーマと例 |
| `docs/` | チュートリアルと画面 |
| `.venv/` | セットアップスクリプトが作ります。消せば最初からやり直せます |
| `CHANGELOG.md` | この版の内容と既知の制限 |

プロジェクト (タスク、契約、学習結果、報告書) は起動時に渡したフォルダに入ります。このフォルダには書きません。

## 使うポートとファイル

- `localhost:10100` — シミュレータの学習サーバ (学習と ④ が接続します)。環境変数 `SIM_LEARNING_PORT` か
  `scripts/gui_resources.json` の `settings.learning_port` で変えられます。
- `localhost:10000` — ⑤ の間の ROS-TCP エンドポイント (コンテナ内かホストで起動します)。
- `~/.config/unirobolab/recent.txt` — 最後に開いたプロジェクト。引数なしで起動するとそこが開きます。

## 困ったとき

| 症状 | 対処 |
|---|---|
| 状態行に「Python 環境が見つからない」 | `scripts/setup_python.sh --training` (Windows は `setup_python.ps1 -Training`) を実行。GUI はこのフォルダの `.venv` を探します |
| Linux でプレイヤーが起動しない | `chmod +x player/UniRoboLab.x86_64`。Vulkan か OpenGL のドライバが必要です |
| Windows で「bash.exe が見つからない」 | Git for Windows を既定の設定で入れてから起動し直してください |
| Windows の SmartScreen に止められる | 実行ファイルは署名していません。「詳細情報 → 実行」で進めます |
| ⑤ で「ROS 2 が使えない」 | 上のセットアップの ⑤ の項を参照。Windows ではこの段階だけ Linux の PC で行ってください |
| 学習が目安より遅い | 目安は空いている PC を前提にしています。シミュレータは多くのコアを使います |

ログ: GUI は `train.log`、`check_runner.log`、報告書をプロジェクトのフォルダに書きます。プレイヤー自身の
ログは Linux では `~/.config/unity3d/UniRoboLab/UniRoboLab/Player.log`、Windows では
`%USERPROFILE%\AppData\LocalLow\UniRoboLab\UniRoboLab\Player.log` です。

## ライセンス

UniRoboLab は Apache License 2.0 で配布します (`LICENSE`)。日本語の表示には、ビルドした PC の
システムフォント (Noto Sans CJK、SIL Open Font License 1.1) から焼いた文字アトラスを同梱しています。
Unity のランタイム (Unity Technologies、
Unity 利用規約に基づく) と、Apache License 2.0 のパッケージ
([Unity_ROS2_Robot_Simulator](https://github.com/REACT-ROBOT/Unity_ROS2_Robot_Simulator)、
ROS-TCP-Connector と URDF-Importer (Unity Technologies、hijimasa によるフォーク)) を同梱しています。
UniRoboLab は個人の独立したプロジェクトであり、Unity Technologies とは無関係で、その承認を受けたものでもありません。

ソースコード: https://github.com/hijimasa/unirobolab
