# UniRoboLab 設計メモ

2026-09-15 時点の方針。会話で決めたことを忘れないために書く。

## 1. 立ち位置

- **並列物理で競わない。** Isaac Lab / mjlab / Genesis は 1 GPU で数千〜数万環境を回す。
  Unity の物理(PhysX / Unity Physics)は CPU 実行で、複数プロセスを束ねても
  桁が違う。NVIDIA 非依存を売りにしても、MJX(JAX)と Genesis が既にその席にいる。
- **後工程を製品にする。** 既存ツールは「学習して ONNX を吐く」で終わる。
  ROS 2 への組み込み、検証、実機投入は毎回手書きで、標準がない。ここが空いている。
- **ライトユーザーが GUI から一連の流れを通せる** ことを差別化点にする。
  Isaac Sim の痛点は RTX 必須・VRAM・インストール・Mac 非対応であり、
  「ワンバイナリでノート PC でも動く」は教育・小規模ラボに刺さる。
- **個人プロジェクト**として進める。会社(REACT)の名義・商標とは切り離す。

## 2. パイプライン

1. GUI でロボット(URDF)とタスクテンプレートを選び、報酬項と観測をチェックボックスで組む
2. 学習。バックエンドは差し替え可能
   - Unity 複数インスタンス(CPU 物理、低自由度タスク向け)
   - 外部(Isaac Lab / mjlab / Genesis)。NVIDIA を持つヘビーユーザーは
     「デプロイ工程だけ」使えるようにして入口にする
3. ONNX と同時に ROS 2 パッケージを生成する
   (観測の組み立て、正規化、関節順、行動スケーリング、ros2_control 接続)
4. 生成したノードをそのまま Unity シミュレータに接続して sim2sim を走らせ、
   成功率・追従誤差を pass/fail で返す
5. 合格したら実機へ。ワークスペース配置と launch まで自動生成

### ポリシー契約(policy contract)

観測・行動・周期の定義を **一箇所(`contract/`)にだけ** 置き、学習環境と
生成コードの両方をそこから生成する。契約が単一の情報源になれば
sim2sim の不一致はほぼ消える。

sim2sim で検出したいのは具体的には次の類のずれ:

- 関節順の不一致
- 単位の不一致(deg/rad、URDF effort の解釈など。シミュレータ側で
  effort の実効トルクが `effort × π/180` になる癖がある)
- 静止中でも速度が 0 にならない類のセンサ癖(runtime import 時の
  jointVelocity 重力バイアス)。契約側で正規化・デッドバンドを持てるようにする
- 制御周期の遅延・ジッタ

## 3. コンポーネントと配布

| 工程 | 実行系 | ユーザーが入れるもの |
|---|---|---|
| GUI・シミュレータ・sim2sim | Unity バイナリ。ONNX 推論は Inference Engine(旧 Sentis)で完結 | バイナリのみ |
| 学習 | PyTorch。C# で RL 学習を回せる成熟実装はない(ML-Agents も学習器は Python) | なし。`uv` の単体バイナリを同梱し、初回に隠しディレクトリへ展開 |
| 生成する ROS 2 パッケージ | C++ ノード + ONNX Runtime | ROS 2 環境のみ |

- 学習器は PyPI に公開しない。バイナリ内から自分の git リポジトリを指定して
  `uv` で入れる。名前の制約は GitHub 上の一意性だけになる。
- torch の CUDA / ROCm ホイール選択をここで吸収する。「AMD でも動く」の実体はこれ。
- ラボの GPU サーバに投げる用途に Docker バックエンドも用意する。

## 4. 既存シミュレータの流用

`Unity_ROS2_Robot_Simulator` をコピーせず、**UPM の git URL + コミットハッシュ**で参照する。
UnitySensors / ROS-TCP-Connector / URDF-Importer が既にこの方式なので揃える。

2026-09-15 に本体側の切り出しが完了(main にマージ済み)。`Packages/` 配下の埋め込みパッケージ:

| パッケージ | 内容 | 同じ manifest に必要なもの |
|---|---|---|
| `com.react-robot.servo-model` | `ServoJointModel` | - |
| `com.react-robot.sdf-world` | `SdfWorldImporter` | - |
| `com.react-robot.urdf-properties` | `CollisionMaterialApplier` | UnitySensors (hijimasa fork) |
| `com.react-robot.hydrodynamics` | `HydrodynamicFloatingObject` | NaughtyWaterBuoyancy (hijimasa fork) |
| `com.react-robot.aerodynamics` | `AeroSurface`、翼素理論プロペラ | `com.react-robot.hydrodynamics` |

`unity/UniRoboLab/Packages/manifest.json` はこれらをハッシュ固定で参照している。
本体を更新したらハッシュを上げる。手元で本体と同時に編集したいときは、本体を隣接 clone にして
`file:../../../Unity_ROS2_Robot_Simulator/Packages/ServoModel` に一時的に切り替える
(git 依存は `package.json` に書けないため、fork 群は manifest 側に置いたままにする)。

プロジェクト設定で本体から引き継いだもの:

- `scriptingDefineSymbols` の `Standalone: ROS2`。ROS-TCP-Connector の `TimeMsg` は
  この定義で `sec` が `int` になる。無いと UnitySensorsROS が `uint`→`int` の変換で
  コンパイルエラーになる(2026-09-15 に実際に踏んだ)。
- `ProjectSettings/DynamicsManager.asset` を本体からコピー(solver 16/4 反復、TGS、
  improved patch friction)。sim2sim の物理を本体と揃えるため。`TimeManager` は
  既定の 0.02 s で本体と同じ。

まだ本体の `Assets/Scripts/` 直下に残っているもの(Assembly-CSharp):
`JointStatePub` / `JointStateSub` / `PhysicsRateController` / `SimulationControl` 系、
ROS メッセージ(`Assets/RosMessages`)。これらは GUI とシーンに結びついているので、
M1 で必要になった分だけ core パッケージとして切り出す。

## 5. 名前と商標

- Unity の商標ガイドラインは製品名・プロジェクト名に「Unity」を含めることを
  認めていない。公開・商用化時に改名を迫られると ID・名前空間の総書き換えになる。
- そのため名前に "Unity" の文字列を入れない。"Uni" は universal / university とも
  読めるが、否定的な意味はなく、暗喩は README の一行目で補う。
- `unirobolab` は 2026-09-15 時点で GitHub の名前一致 0 件、PyPI 空き。
- 表記は UniRoboLab(Uni / Robo / Lab に切れる)。

## 6. マイルストーン

- **M0** リポジトリ雛形 — 完了(2026-09-15)
- **M1** 既存の ONNX ポリシー + 契約を入力に ROS 2 パッケージを生成し、
  Unity で sim2sim して合否を出す — **配線確認ポリシーで完了(2026-09-16)**。7 章参照
- **M2** 学習 GUI。Unity 複数インスタンスのベクトル化環境ラッパー、
  手動ステップ(`Physics.Simulate`)と決定論性、`uv` 同梱の学習器、
  学習曲線と報酬項ごとの寄与の表示、sim2sim 失敗時の原因診断
- **M3** 実機デプロイ。まずは自前シミュレータ + 実機 1 構成に絞る
  (ros2_control のインターフェースは種類が多く、保守負担が膨らむ)。
  生成ノードの `command_mode=ros2_control_commands` はここで検証する

最初のタスクは低自由度に絞る: アーム到達、移動ロボット走行、SG90 級の小型機。
四足歩行の大規模学習は外部バックエンドに任せる。

## 7. M1 の実装と結果

### 構成(`python/src/unirobolab/`)

| モジュール | 役割 |
|---|---|
| `contract.py` | 契約の読み込みと配置(各項のサイズ・オフセット)の導出。ROS 非依存 |
| `test_policy.py` | 配線確認用 ONNX(`command` 観測を `joints` 行動へ写す線形層)。学習なし |
| `generator.py` | ament_python パッケージ生成。契約 JSON と ONNX を share に同梱 |
| `ros2/policy_node.py` | **ノード本体。生成物へそのまま複製される。** 契約を実行時に読む |
| `ros2/sim2sim.py` | 評価器。生成パッケージを起動し、目標列を流し、合否とレポートを出す |
| `cli.py` | `unirobolab show / make-test-policy / gen / sim2sim / train` |

設計上の要点:

- 生成物にロジックを埋め込まない。ノードは 1 ファイルで、sim2sim でも実機でも同じものが動く。
  「観測・行動の配線の実装が 1 つしかない」ことが契約の単一情報源を支える。
- 観測の処理順は deadband → ×scale + offset → clip、行動は clip → ×scale + offset → publish。
  契約スキーマに明記した。
- 関節順は契約の `robot.joints` が正準。`joint_states` の並びが違っても名前で引き直す
  (servo_demo は実際に `[cheap_joint, ideal_joint]` で届き、契約は `[ideal, cheap]`)。
- 指令は `ros.command_mode` で切り替える。`joint_state_topic` は Unity の
  `/<ns>/joint_command` へ直接(topic_based_ros2_control と同じ入口)、
  `ros2_control_commands` はコントローラの `/commands`(M3 で検証)。
- ノードは 1 Hz で `policy/status`(JSON)を出す: 実測周期、観測の鮮度(最大・平均)、
  推論時間、契約エラー。評価器はこれで node 側の異常を拾う。

### 実行手順(servo_demo)

```bash
# ホスト
python/.venv/bin/unirobolab make-test-policy contract/examples/servo_demo.json
python/.venv/bin/unirobolab gen contract/examples/servo_demo.json --out generated --overwrite
scripts/sim2sim_container.sh start            # ROS 2 + シミュレータのコンテナ(隣の Unity_ROS2_sample を使う)
# コンテナ内(docker exec unirobolab-sim2sim-jazzy bash)
pip3 install --user --break-system-packages onnxruntime
bash /home/unity/unirobolab/scripts/servo_demo_bringup.sh   # endpoint → sim → start → spawn(コントローラなし)
cd /home/unity/unirobolab/generated && colcon build --base-paths servo_demo_policy \
    --build-base ws/build --install-base ws/install --symlink-install && source ws/install/setup.sh
cd /home/unity/unirobolab && PYTHONPATH=python/src:$PYTHONPATH python3 -m unirobolab sim2sim \
    contract/examples/servo_demo.json --pkg servo_demo_policy \
    --scenario contract/examples/servo_demo.sim2sim.json --out generated/sim2sim_out
```

注意: `PYTHONPATH=python/src` と書くと ROS の `PYTHONPATH` を消して `rclpy` が見つからなくなる。
必ず `:$PYTHONPATH` を付ける。

### 結果(2026-09-16、simulator v1.4.0、ROS 2 Jazzy)

配線確認ポリシー(目標 = 指令)で ±0.5 rad と 0 のステップ、各 4 s 保持:

| チェック | 結果 |
|---|---|
| joints_present | ok(並び違いを名前で解決) |
| node_alive | ok、契約エラーなし |
| rate | 50.0 Hz(目標 50) |
| obs_fresh | 最悪 107 ms、平均 55 ms(上限 120 ms、下記) |
| tracking | ideal_joint 誤差 1e-4 rad、cheap_joint 3〜9 mrad(バックラッシ半幅 26 mrad 以内) |
| finite | ok |

否定テスト(意図的に壊した契約)は `joints_present` と `tracking` で FAIL になることを確認した
(関節名の誤り、行動スケールの不一致)。

### 発見と解決: joint_states が 100 ms ごとにまとめて届いていた

最初の計測では `/ServoDemo/joint_states` の到着間隔が p50 0.3 ms、p95 100 ms で、
約 3 通が 100 ms ごとにバーストで届いていた。原因は描画周期で、本体の
`FrameRateController.Start` が `Application.targetFrameRate = 10` を固定している。
10 FPS では Unity が 1 フレームに 50 Hz の物理ステップを 5 回連続で回すため、
FixedUpdate から出る配信は壁時計上まとめて出て、指令の消費も 1 フレームに 1 回になる。
ROS-TCP の束ね処理ではなかった。

解決は `simulation_resources.json` の `settings.target_fps`(`SIMULATION_RESOURCES_CONFIG` で指定)。
`scripts/sim2sim_resources.json` に置き、立ち上げスクリプトが環境変数で渡す。

| target_fps | joint_states 間隔 p50 / p95 / max | 観測の鮮度 最悪 / 平均 |
|---|---|---|
| 10(既定) | 0.3 / 100 / 120 ms | 107 / 55 ms |
| 60 | 40 / 41 / 50 ms | 37 / 24 ms |
| 200 | 40 / 41 / 45 ms | 37 / 24 ms |

60 FPS 以上で 30 Hz 配信(JointStatePub の既定)どおりの到着になり、200 FPS でも変わらない。
残る 40 ms は配信周期そのもので、50 Hz ポリシーには 1 周期遅れ相当。
これは学習環境(8 章)の 25 Hz(2 物理ステップ/行動)で吸収する。
sim2sim シナリオの `max_obs_age_s` は 0.06 にした。

本体側への提案: 既定 10 FPS はデモ用の値で、ROS 2 連携では常に不利になる。
`FrameRateController` の既定を 60 に上げるか、`joint_states` の publishRateHz を
URDF から設定できるようにするのがよい。

## 8. M2 の第一段階: シミュレータで学習し、生成→sim2sim まで通す

GUI は後回しにし、まず「学習したポリシーが同じ配線で配備される」ことを通す。

### 学習環境(`python/src/unirobolab/ros2/unity_env.py`)

- Gymnasium 互換。シミュレータを `set_simulation_state(PAUSED)` にして、
  1 行動ごとに `step_simulation(N)` で物理を N ステップ進める(壁時計に依存しない)。
- 観測・行動は `policy_node.py` の `process_obs_term` / `process_action_term` をそのまま使う。
  学習側と配備側で配線の実装が一つ。
- `reset_simulation(SCOPE_STATE)` で関節をゼロに戻し、目標を一様乱数で引く。
- 観測は step 応答の後に届く `joint_states`(30 Hz sim 時間)を待って取る。
  25 Hz(2 ステップ/行動)なら毎行動で必ず 1 通以上届く。step 応答とトピックは経路が
  違うので、応答後に最大 200 ms だけ待つ(実測では待ちゼロ)。
- タスク定義(`*.train.json`)は契約と分ける: 目標範囲、エピソード長、行動あたりの物理ステップ数、
  報酬項の重み(`tracking_l1` / `tracking_l2` / `action_rate` / `velocity`)。

### 学習器(`python/src/unirobolab/train.py`)

- stable-baselines3 の PPO(MLP 64x64、CPU)。
- ONNX 書き出しは方策の平均出力(決定論)だけを `features → mlp_extractor.forward_actor → action_net`
  で包み、契約の `input_name` / `output_name` で `torch.onnx.export`。
- `progress.csv` にエピソードごとの収益・長さ・最終誤差・報酬項ごとの寄与を書き、
  `learning_curve.png` に描く。学習後に決定論ロールアウトで `eval.json`。

### 実行(コンテナ内、`SIM_SETTINGS=scripts/train_resources.json` で target_fps 200 / time_scale 10)

```bash
docker exec -e SIM_SETTINGS=/home/unity/unirobolab/scripts/train_resources.json unirobolab-sim2sim-jazzy \
    bash /home/unity/unirobolab/scripts/servo_demo_bringup.sh
PYTHONPATH=python/src:$PYTHONPATH python3 -m unirobolab train contract/examples/servo_demo_rl.json \
    --config contract/examples/servo_demo_rl.train.json --out generated/runs/servo_demo_rl
python/.venv/bin/unirobolab gen contract/examples/servo_demo_rl.json --out generated --overwrite   # ホスト
# 以降は 7 章と同じ sim2sim(契約は servo_demo_rl、25 Hz)
```

### 学習環境の輸送経路と往復時間(2026-09-16 追加)

最初の実装(joint_command トピック + step_simulation + joint_states トピック)は 17.8 steps/s
だった。本体に `step_and_observe` サービス(`simulation_extra_interfaces/srv/StepAndObserve`:
指令 → N ステップ → 関節状態を 1 往復)を追加したが、それだけでは 18.7 steps/s で変わらなかった。
`ros2/step_latency_probe.py` で steps 数を変えて呼ぶと、**往復の固定分が約 48 ms** で、
ステップ数にほぼ依存しないことが分かった。

原因は ROS-TCP-Connector の TCP ソケットで Nagle アルゴリズムが有効なこと。コネクタは
1 メッセージをシステムコマンド(JSON)と本文の複数回の write で送るので、2 回目の write が
1 回目の ACK 待ちになり、Linux の遅延 ACK(最大 40 ms)がそのまま乗る。
`TcpClient.NoDelay = true` の 1 行で往復 48 ms → 10 ms(hijimasa/ROS-TCP-Connector の
`tcp-nodelay` ブランチ、本体 manifest はそのハッシュを指す)。

| 構成 | 往復固定分 | 1 物理ステップ | 環境スループット(2 ステップ/行動) |
|---|---|---|---|
| トピック経路、target_fps 200 | - | - | 17.8 steps/s |
| step_and_observe、Nagle 有効、200 FPS | 48 ms | 1.2〜2 ms | 18.7 steps/s |
| step_and_observe、NoDelay、200 FPS | 13 ms | 1.9 ms | 54.9 steps/s |
| step_and_observe、NoDelay、500 FPS | 8 ms | 2.0 ms | **90.5 steps/s** |

ステップ中は 1 フレームに 1 物理ステップしか進まない設計(step_simulation の決定論のため)なので、
1 ステップの費用はフレーム時間で決まる。学習用の設定は `scripts/train_resources.json`
(target_fps 500、time_scale 10)。残る 8 ms はコネクタ受信スレッドの 10 ms ポーリング
(`ROSConnection.SleepTimeSeconds`)とエンドポイントの処理で、次の改善点。

学習環境は `/step_and_observe` があればそれを使い、無ければトピック経路に自動で戻る
(`task.use_step_and_observe`)。

### 結果(2026-09-16、servo_demo_rl、simulator v1.4.0)

学習は 2 回。1 回目(20k ステップ、SB3 既定の log_std_init=0、std 1 rad)は最終誤差 0.18 rad で
未収束、sim2sim は ideal_joint の追従で FAIL(0.12〜0.16 rad、目標を超えて止まる)。
2 回目(40k ステップ、log_std_init=-1.5、std 0.22 rad)は 300 エピソードで収束した。

| 指標 | 1 回目 20k | 2 回目 40k |
|---|---|---|
| 学習時間(17.8 steps/s) | 19 分 | 37 分 |
| 最終 100 エピソードの最終誤差(学習中) | 0.18 rad | 0.054 rad |
| 決定論評価の整定誤差 | 0.127 rad | 0.026 rad |
| sim2sim(25 Hz、実時間、target_fps 60) | FAIL(tracking, obs_fresh) | **PASS** |

3 回目(同じ設定、`step_and_observe` + NoDelay の本体ブランチで学習): 40k ステップが 507 s
(78.9 steps/s、PPO の更新込み)、決定論評価の整定誤差 0.016 rad、sim2sim(トピック経路)PASS:
ideal_joint 2〜15 mrad、cheap_joint 37〜43 mrad、観測鮮度 最悪 44 ms / 平均 21 ms
(NoDelay はトピック経路の配備側にも効き、76 ms → 44 ms)。
サービス経路で学習した方策がトピック経路の配備で通ることを確認できた。

2 回目の sim2sim: ideal_joint 誤差 4〜9 mrad、cheap_joint 55〜57 mrad(バックラッシ幅 90 mrad の
半分に張り付く分)、実測周期 25.0 Hz、観測鮮度 最悪 76 ms / 平均 40 ms。
学習曲線は `docs/images/servo_demo_rl_learning_curve.png`。

これで「契約 → 学習 → ONNX → パッケージ生成 → sim2sim 合格」が同じ配線で一周した。
学習中の評価(0.026 rad)と sim2sim の追従(0.004〜0.009 rad)が矛盾しないのは、
学習環境と配備ノードが同じ処理関数で観測・行動を作っている効果。

### 学んだこと

- PPO の初期探索幅は契約の単位で決める。位置目標が rad なら std 1 は広すぎる。
  `train.log_std_init` を設定項目にした(既定 -1.5)。
- 25 Hz 契約では 2 周期(80 ms)の鮮度上限が厳しすぎる(30 Hz 配信の位相で最悪 82 ms)。
  シナリオごとに `max_obs_age_s` を持たせ、理由を `_note` に書く運用にした。
- 学習環境の観測待ちは「step 応答の後に新着を待つ」と 1 手遅れて 2 秒のタイムアウトを
  毎回踏む(1 回目の試走で 0.5 steps/s)。step 前の受信通番を覚え、それより新しいものを取る。

### M2 の残り

- GUI(Unity 側): 契約とタスク定義の編集、学習の起動と学習曲線・報酬項の表示、sim2sim 結果の表示
- スループット: 90 steps/s まで来た(`step_and_observe` + NoDelay + 500 FPS)。次は
  コネクタ受信スレッドの 10 ms ポーリング(`ROSConnection.SleepTimeSeconds`)の短縮と、
  複数インスタンスのベクトル化
- `uv` 同梱の学習器の配布(今は派生 Docker イメージで代替)
- sim2sim 失敗時の原因診断(今は各チェックの理由文まで)
