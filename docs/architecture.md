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

## 9. 学習は ROS 2 を通さない: 直結の学習サーバ(2026-09-16)

8 章の経路(ROS 2 サービス)は往復の固定分 8 ms が ROS-TCP-Endpoint(Python)と DDS で、
物理 2 ステップの 4 ms より大きい。学習には ROS 2 は要らない(配備と sim2sim のための経路)ので、
本体に学習専用の直結 TCP サーバを足した(`Assets/Scripts/SimulationLearningServer.cs`、
ブランチ learning-server)。

- 有効化は `settings.learning_port` か環境変数 `SIM_LEARNING_PORT`。既定は無効で、既存の挙動には触れない。
- 1 往復で「複数エンティティへの関節指令 → N ステップ → 全エンティティの関節状態」。
  指令の適用・ステップ・観測は step_and_observe と同じ関数(JointStateSub.ApplyCommand、
  RunSteps、BuildObservation)。プロトコルは長さ付きの小さなバイナリ(op: INFO / RESET / STEP /
  PING / PAUSE)。
- ベクトル化は「複数インスタンス」ではなく「1 シーンに複数エンティティ」。ステップ中は
  1 フレームに 1 物理ステップなので、K 体を束ねてもフレーム数は増えず、K 倍近くスケールする。
  `scripts/servo_demo_bringup.sh` の `N_ENTITIES=K` が `ServoDemo_0..K-1` を 0.6 m 間隔で並べる
  (spawn_entity のパラメータ名は `robot_name`。サンプルの launch は `name` を渡していて効いていない)。
- Python 側は `direct/client.py`(プロトコル)、`direct/vec_env.py`(SB3 の VecEnv)、
  `task.py`(目標と報酬、ROS 経路の環境と共通化)、`obs_math.py`(観測・行動の処理。
  `ros2/policy_node.py` の複製と一致することを `tests/test_obs_math.py` が確認)。
- `unirobolab train --transport direct --n-envs K`。ROS 経路は `--transport ros` で残す。

### 計測(2026-09-16、servo_demo、-batchmode -nographics、physics 50 Hz、2 ステップ/行動)

| エンティティ数 | 往復/s | env steps/s | 1 往復 |
|---|---|---|---|
| 1 | 165 | 165 | 5.8 ms |
| 8 | 155 | 1,241 | 5.5 ms |
| 16 | 136 | 2,180 | 5.8 ms |
| 32 | 100 | 3,189 | 6.4 ms |

ROS 経路の 90 steps/s に対し、1 体でも 1.8 倍、16 体で 24 倍。1 往復は物理 2 ステップ分の
フレーム待ち(1 フレーム 1 ステップの設計、target_fps 500 でも 2000 でも約 2.5 ms/フレーム)が
ほぼ全てで、TCP と直列化は 1 ms 未満。32 体で往復が伸び始めるのは物理そのもの
(32 体 × 3 リンク)の費用。さらに上げるには「ステップ中は複数物理ステップを 1 フレームで回す」
モードが要るが、FixedUpdate 依存のコンポーネント(ServoJointModel など)との整合を取る必要があり、
別の段階にする。

PPO 側の注意: n_steps は env ごとなので、16 体では n_steps 50(ロールアウト 800)にする。
1 体用の n_steps 400 のままだと 40k ステップで更新が 6 回しか回らず学習しない
(最終誤差 0.31 rad で失敗した)。

### 結果: 直結経路で学習 → ROS 2 経路で sim2sim PASS(2026-09-16)

`unirobolab train ... --transport direct --n-envs 16`(400k ステップ、n_steps 50、log_std_init -1.5):

| 指標 | ROS 経路 1 体(3 回目) | 直結 16 体 |
|---|---|---|
| 学習時間 | 40k ステップ / 507 s | 400k ステップ / 216 s(1,854 env steps/s、PPO 更新込み) |
| 決定論評価の整定誤差 | 0.016 rad | **0.006 rad** |
| sim2sim(トピック経路、25 Hz) | PASS、ideal 2〜15 mrad | **PASS、ideal 0.3〜5 mrad、cheap 4〜21 mrad** |

10 倍のサンプルを 4 割の時間で回せるようになり、方策も精度が上がった。直結経路で学習した方策を
ROS 2 経路(生成パッケージ + トピック)で配備しても通る、が確認できたので、
「学習は直結、配備は ROS 2」を既定にする。学習曲線は `docs/images/servo_demo_rl_learning_curve.png`。

### 残り

- `PAUSE` op を入れたプレイヤーの再ビルド(Unity ライセンスの一時的な失敗で未ビルド。それまでは
  `set_sim_state pause` を ROS で先に呼ぶ。学習環境は "unknown op" を検出して警告する)
- ~~ステップ中に複数物理ステップを 1 フレームで回すモード~~ → 実装した(下記)
- GUI、uv 同梱配布、M3(ros2_control_commands)

## 10. M3 の第一段階: ros2_control 経由の配備を sim2sim で通す(2026-09-16)

実機は ros2_control で動かすのが普通なので、生成ノードの `command_mode=ros2_control_commands`
(コントローラの `/commands` に `Float64MultiArray`)を servo_demo で検証した。

- 契約 `contract/examples/servo_demo_rl_ros2control.json`: `ros.command_mode` と
  `ros.controller_name` 以外は `servo_demo_rl.json` と同じ。方策は直結学習のもの。
- `gen` はこのモードのとき `config/controllers.yaml` も出す(joint_state_broadcaster と、
  行動項の `ros2_control_interface` 型のコントローラ。関節順は行動項と同じ)。
  ノードが出す配列の順とコントローラの関節順が同じ生成元から出るので、ここでずれない。
- `scripts/ros2_control_servo_demo.launch.py`: robot_state_publisher + ros2_control_node
  (topic_based_ros2_control → Unity の `/ServoDemo/joint_command`)+ spawner。
  サンプルの launch から JTC と commander を外したもの。

結果(sim2sim、25 Hz、直結学習の方策): **PASS**。ideal_joint 3〜6 mrad、cheap_joint 11〜37 mrad。
経路は ノード → `/ServoDemo/joint_group_position_controller/commands` → controller_manager(100 Hz)
→ topic_based → `/ServoDemo/joint_command` → Unity。観測の `/ServoDemo/joint_states` は Unity(30 Hz)と
joint_state_broadcaster(100 Hz)の両方が同じ名前で出すので、ノードは両方を受ける
(実機では broadcaster だけになる)。鮮度は最悪 7 ms。

これで契約からの 3 経路(直結学習、トピック配備、ros2_control 配備)が同じ方策で通った。
M3 の残りは実機 1 構成での実行と、ros2_control の状態を観測に使う場合の話(effort など)。

## 11. ステップ中に複数の物理ステップを 1 フレームで回す(2026-09-16)

本体 `settings.stepping_steps_per_frame`(ブランチ multi-step-frames)。既定 1 は従来どおり。
k にすると残り 1 ステップまでを 1 フレーム k ステップで進め、最後は従来どおり 1 フレーム
1 ステップで刻む。FixedUpdate 依存のコンポーネント(サーボモデル、外乱、センサ)はステップごとに
従来どおり呼ばれるので忠実度は変わらない。

落とし穴: 最初は timeScale を 100 に上げて `maximumDeltaTime` で k ステップ分に切る方式で書いたが、
Unity は `maximumDeltaTime` を `fixedDeltaTime` 未満に絞れない(切り上げる)ため、実フレーム時間
× 100 の分だけ回ってしまい、8 要求で 12 回、100 要求で 112 回になった。`Time.captureDeltaTime`
で 1 フレームの経過時間そのものを k × fixedDeltaTime に固定する方式に直して、
1〜100 ステップすべて要求どおりになった(`direct/step_check.py` で物理時間から検証。
学習サーバの `sim_time` を物理時間 `Time.fixedTimeAsDouble` にしたのはこのため)。

| 行動あたり物理ステップ | k=1(従来)1 往復 | k=8 1 往復 | 8 体の env steps/s(k=1 → k=8) |
|---|---|---|---|
| 2 | 4.9 ms | 3.8 ms | 1,316 → 1,527 |
| 10 | 20.8 ms | 5.9 ms | 363 → 1,029 |

2 ステップ/行動では 1 往復にフレームが 2 枚要る構造(要求の受付と応答)は変わらないので
効きは 2 割強、10 ステップ/行動のような高周波物理・低周波制御では 3.5 倍。
`scripts/train_resources.json` は k=8 にした。

## 12. 関節以外の観測: 基体の姿勢・速度と移動ロボット(2026-09-16)

学習サーバの応答に、エンティティごとの基体状態(位置 xyz、姿勢 xyzw、線速度・角速度、
いずれも ROS 座標系・world)を足した(本体ブランチ base-state)。`get_entity_state` と同じ
`BuildEntityState` から取る。RESET は関節のゼロ姿勢化に加えて基体をスポーン時の姿勢に
置き直す(`reset_simulation` SCOPE_STATE のエンティティ単位版)。

契約の観測ソース `base_lin_vel` / `base_ang_vel` / `projected_gravity` / `imu_orientation` を
学習環境と配備ノードの両方で実装した(いずれも基体座標系)。新しく `base_goal_xy`(2)を足した:
地面上の目標点を基体座標系で見た相対ベクトルで、配備ノードは目標トピックの world (x, y) と
姿勢トピック(`ros.ground_truth_topic`、PoseStamped。実機では自己位置推定に差し替える)から作る。
速度はノード側では姿勢の差分で作る。

タスク種別 `base_target`(`task.py`): スポーン点の周り半径 1〜2.5 m に目標を引き、
`base_progress`(距離の減少)、`base_reached`(到達ボーナス、到達で終了)、`action_rate` で報酬。
`contract/examples/diffbot_rl.json`(差動二輪、車輪速度指令 ±10 rad/s、10 Hz、5 物理ステップ/行動)。

立ち上げは `ROBOT_XACRO` で任意の xacro を `use_sim:=true` で展開し、`ROBOT_STRIP_SENSORS=1` で
`<gazebo>` 要素(LiDAR・カメラ)を落として体数分の負荷を避ける。16 体を 6 m 間隔で並べる。

落とし穴: スポーン位置を `x:=0` のように整数で渡すと spawn_entity の double パラメータと
型不一致で落ちる(`float()` で出す)。servo_demo では間隔 0.6 で偶然 float だった。

計測: 16 体の diffbot で 1 往復 60 ms(5 物理ステップ)。servo_demo の 4 ms と違い、
地面と接触する 16 体の物理そのものが 1 ステップ 10 ms 前後かかる。
258 env steps/s。ソルバ反復(settings.solver_iterations)を下げる余地はある。

### 発見: 従来の step_simulation も物理が重いと要求より多く進んでいた

diffbot 16 体(1 物理ステップ 10 ms 前後)で `step_check` を回すと、チャンク進行なし
(`stepping_steps_per_frame=1`、従来設計そのまま)でも 3 要求で 4 回、100 要求で 107 回進んだ。
原因は 11 章と同じで、`maximumDeltaTime = fixedDeltaTime / time_scale` は Unity に
`fixedDeltaTime` へ切り上げられ、実フレーム 80 ms × time_scale 10 で 1 フレームに数ステップ分が
積まれる。servo_demo はフレームが 2.5 ms と軽く、偶然 1 ステップに収まっていた。
最後の 1 フレーム 1 ステップの刻みも `captureDeltaTime = fixedDeltaTime` に固定して解決
(ステップ中は time_scale を使わない)。`step_simulation` / `simulate_steps`(ROS 2 経路)にも
同じ修正が効く。

### 結果: diffbot の目標到達を直結で学習し、ROS 2 経路の sim2sim で通す(2026-09-16)

`unirobolab train contract/examples/diffbot_rl.json --config contract/examples/diffbot_rl.train.json
--transport direct --n-envs 16`(600k ステップ、2,576 s、233 env steps/s):

- 学習中の最終距離は 80 エピソードで 1.9 m → 0.32 m、以後 0.11 m 前後(到達半径 0.15 m で終了)。
  エピソード長は平均 19 ステップ(1.9 s)。決定論評価の最終距離 0.10 m。
- sim2sim(ROS 2 トピック経路、`ros.ground_truth_topic` から基体観測、10 Hz): **PASS**。
  目標 3 点(前 1.5 m、左 1.5 m、後左 1.4 m)で最終距離 0.08 / 0.22 / 0.12 m(許容 0.25 m)。
- 途中で sim2sim が「ONNX 入力 12 ≠ 契約 10」で FAIL した。配備ノードの観測サイズ表に
  `base_goal_xy` が無かったためで、契約と実装のずれを sim2sim が捕まえた例。

学習曲線は `docs/images/diffbot_rl_learning_curve.png`。これで関節タスク(servo_demo)と
基体タスク(diffbot)の両方で「直結で学習 → ROS 2 で配備」が通った。

## 13. 学習時間: 早期終了、ROS 2 なしのプール、CPU の実態(2026-09-16)

diffbot の 43 分は、収束(32k ステップ、125 s)の 19 倍の予算を与えていたのが主因だった。

### 早期終了(`train.early_stop`)
ロールアウト末に直近 `window` エピソード(env 数 × `episodes_per_env` 以上に自動で広げる)の
`success_rate` か `final_abs_err` を見て打ち切る。`total_timesteps` は上限のまま。
diffbot 16 体: 43 分 → **172 s**(56k ステップで成功率 92%)。

### ROS 2 なしのプール(`scripts/sim_pool.sh` + 学習サーバの SPAWN op)
学習サーバに SPAWN(URDF のパス、名前、位置・yaw)を足し、学習器が自分でロボットを並べる
(`train --spawn-urdf`)。エンドポイントもスポーンサービスも要らない。`sim_pool.sh start K` で
K プロセスをポート 10100.. に立て、`train --instances K --n-envs M` が `PoolVecEnv`(スレッドで
K 本の接続を同時にステップ)で束ねる。xacro は `sim_pool.sh urdf <xacro> [args]`(`<gazebo>` を落とす。
diffbot は `use_gnss:=false use_magnetic_guide:=false` も付けないと GNSS 配信が毎フレーム例外を投げる。
ただし速度には効かなかった)。

### CPU の実態と複数インスタンスの効き

| 構成 | env steps/s | 備考 |
|---|---|---|
| K=1 × 16 体(既定ワーカー 29) | 465 | Job.Worker 29 本で約 15 コア分、メイン 1 コア |
| K=1 × 16 体(ワーカー 2) | 205 | ワーカーを減らすと遅い = 物理は 1 シーン内で並列に解かれている |
| K=4 × 16 体(既定) | 813 | 1.75 倍。116 本のワーカーが 30 コアを取り合う |
| K=8 × 8 体(ワーカー 2〜3) | 909〜950 | 約 2 倍で頭打ち |

- 1 インスタンスで既に十数コアを使う(PhysX の並列ソルバと、Unity のジョブワーカーのスピン待ち)。
  16 体 × 5 ステップに 34 ms = 1 体 1 ステップ 0.43 ms で、これがこのマシンの物理の壁。
  複数インスタンスは 2 倍程度までしか伸びない。`sim_pool.sh` は既定でワーカー数を cores/K に分ける。
- 「同一ワールドに並べる」は既にそうしている(1 シーン K 体)。プロセス並列は「物理シーンの
  ステップ呼び出しがメインスレッドで逐次」という制約を回避する手段で、ワールドを分けたい
  わけではない。桁を変えるには GPU バッチ物理(Isaac Lab / Newton / MuJoCo Warp)側で学習する分業が要る
  (`docs/unity6-gpu-physics-survey.md`)。
- 計測の落とし穴: `ps` の %cpu は累積平均で、停止直前のプロセスを拾って「アイドルで 27 コア」と
  誤認した。`top -d` の区間計測で見直した。

## 14. 優先順位 1: 外部で学習した方策の取り込みと、実機向けの観測・行動(2026-09-16)

配備層を「どこで学習した方策でも」にするための拡張。

### 契約の拡張
- `scale` / `offset` は要素ごとの配列も可(Isaac Lab の `joint_pos_rel` = q − q_default や、
  関節ごとの行動スケールを表す)。処理順は変わらず、`obs_math` / 配備ノードは numpy の
  ブロードキャストで扱う。`_` で始まるキーは注釈として許す。
- `command` 項に `ros_type: twist`(`[vx, vy, wz]`、`geometry_msgs/Twist` で受ける)。
- 行動ターゲット `base_twist`(size 2 = `[vx, wz]`、3 = `[vx, vy, wz]`)。配備ノードは
  `ros.cmd_vel_topic` に Twist を出す。学習環境(Unity は関節指令しか受けない)では
  `task.diff_drive {wheel_radius, wheel_separation, left_joint, right_joint}` で車輪速度に変換する。
- `ros.imu_topic`(`sensor_msgs/Imu` → 角速度・姿勢)、`ros.odom_topic`(`nav_msgs/Odometry` →
  基体座標系の線速度・位置)。指定があれば ground_truth より優先し、両方あれば ground_truth は購読しない。

### Isaac Lab 取り込み器(`unirobolab import-isaaclab`)
`logs/rsl_rl/<task>/<run>/params/env.yaml`(ManagerBasedRLEnvCfg のダンプ)から契約を作る。
- `sim.dt` × `decimation` → 周期。観測グループ(既定 `policy`)の項を yaml の順に
  `base_lin_vel` / `base_ang_vel` / `projected_gravity` / `generated_commands`(速度指令 → `command`、
  twist)/ `joint_pos_rel` / `joint_vel_rel` / `last_action` へ写す。`_rel` は `init_state.joint_pos` の
  正規表現テーブルを関節順で解決して `offset = −q_default × scale`。
- 行動 `JointPositionAction` 等: `scale`(スカラーか正規表現テーブル)、`use_default_offset` →
  `offset = q_default`。観測と同名なら `_action` を付ける。
- **関節順は利用者が渡す**(`--joints`)。Isaac Sim の関節順は USD の走査順で URDF と一致しないため、
  env.yaml からは分からない。height scan や画像は契約に無いので拒否する。
- `python/tests/fixtures/isaaclab_anymal_flat_env.yaml`(Anymal-C flat 相当)で単体テスト。
  Isaac Lab 実機での通し検証は未実施(環境が無い)。配線確認ポリシーは command と joints の
  サイズが等しい契約向けなので、歩行の契約には使えない。

### 学習環境の追加(優先順位 2 の一部)
- `control.action_latency_steps` を学習環境で実装: 行動は N 周期遅れて適用され、
  `last_action` 観測はアクチュエータが受け取った方を返す。
- `task.obs_noise = {項名: 標準偏差}`: スケール前の生値にガウスノイズ。

### 目標に「留まる」ことを学習させる(2026-09-16)

diffbot の配備で目標付近の最終距離が 0.22〜0.26 m とふらつき、許容 0.25 m を僅差で割った。
原因は学習が「到達半径に入った時点でエピソード終了」だったため、到達後の挙動が未学習だったこと。
配備ではノードは目標でリセットされずに動き続ける。`task.terminate_on_reach`(既定 false)を足し、
エピソードは最後まで走らせて `base_reached` を半径内の毎ステップに払う(「留まる」報酬)。
成功判定は「エピソード終了時に半径内」。再学習は早期終了付きで 211 s(92.8k ステップ)、
決定論評価の最終距離 0.067 m(終了型の 0.22 m から改善)。

### sim2sim の頑健性シナリオ(優先順位 2)
シナリオに `random_goals`(範囲と個数)と `disturbances`(保持中の指定時刻に本体の
`apply_link_wrench` で力・トルクを与える)を足した。`servo_demo_rl.robust.sim2sim.json` は
ランダム目標 5 点と、各保持の 1 s 後に cheap 側アームへ 0.3 N·m × 0.2 s の蹴りを入れる。

### sim2sim → 学習へ戻すループの例(2026-09-16)

「留まる」学習後も diffbot の 3 点目(後左 1.4 m、約 135° 旋回)は 0.253 m で許容 0.25 を僅差で割った。
配備の観測は最悪 45 ms 古く ground truth にジッタもあるので、`task.obs_noise`(基体速度 0.02 / 0.05、
目標 0.02、車輪速度 0.2 の標準偏差)を入れて再学習(226 s、早期終了 100.8k ステップ、評価 0.071 m)。
sim2sim は 2 回とも PASS(0.017 / 0.108 / 0.113 m と 0.020 / 0.103 / 0.208 m)。
sim2sim の不合格を学習設定に返す、という運用の最初の実例。

## 15. 優先順位 3: Unity から方策を動かす(ライブ実行器と Policy パネル、2026-09-16)

### 判断: 推論は Unity の中でやらない
Inference Engine(旧 Sentis)2.6 の ONNX 変換はエディタ専用(`Editor/ONNX/ONNXModelConverter.cs`)で、
ビルドしたプレイヤーの `ModelLoader.Load(path)` が読めるのは変換済みの `.sentis` だけ。
「任意の ONNX を配布バイナリだけで動かす」は成立しない。代案の ONNX Runtime ネイティブ同梱は
配線の実装が 4 つ目になり保守負荷も増える。そこで:

- **ライブ実行器 `unirobolab live`**(`direct/live.py`): 学習サーバに PLAY(op 7)と
  「再生中の指令適用+観測」(STEP steps=0)を足し、Python 側が実時間で方策周期ごとに
  観測 → onnxruntime → 指令を回す。観測・行動は `obs_math` を使うので配線の実装は増えない。
  目標は `--goal` と stdin(`goal v1 v2 ...`、`stop`)。状態行を毎秒 JSON で stdout に出す。
- **Policy パネル**(本体 `Assets/Scripts/PolicyPanel.cs`、ブランチ live-policy): 契約・ONNX・
  エンティティ・目標を入力し、Run で上のコマンドを子プロセスとして起動、Set goal で stdin、
  Stop で終了。コマンドは `settings.policy_runner_command`(既定 `python3 -m unirobolab live`)と
  `settings.policy_runner_env`(PYTHONPATH など)。学習サーバが無効なら案内を出す。
  ヘッドレス検証用に `SIM_POLICY_AUTORUN="契約|ONNX|エンティティ|目標|秒数"`。
  同梱予定の uv 環境の python を `policy_runner_command` に指すと、利用者は Python を意識しない。

結果(ROS 2 なし): servo_demo_rl の方策で目標 (0.5, 0.5) → 最終誤差 3.9 mrad、stdin で (−0.5, −0.5) に
変更 → 1.4 mrad、25 Hz。diffbot_rl で (1.5, 0) → (0, 1.5)、8 s で 0.12 m、10 Hz。

検証(ヘッドレス、本体ブランチ live-policy): `SIM_POLICY_AUTORUN` でシミュレータ自身が
`python3 -m unirobolab live` を起動し、servo_demo_rl の方策が 25 Hz で目標 (0.4, −0.4) に
最終誤差 6.7 mrad。Policy パネルの UI 本体は実行時生成の uGUI で、画面での操作確認は
ウィンドウ表示できる環境でお願いしたい(ヘッドレスでは UI を作らない)。

## 16. 優先順位 4: 実機向けの安全機構(2026-09-16)

契約に `safety` 節を足し、配備ノードが実行時に守る。すべて任意で、状態(`policy/status`)に載せる。

| 項目 | 挙動 |
|---|---|
| `max_obs_age_s` | joint_states(基体観測を使うならそれも)がこれより古いと指令を止める。既定 3 周期 |
| `estop_topic`(`std_msgs/Bool`) | true の間は指令を止める。既定 `/<ns>/policy/estop` |
| `stop_action` | 停止時に `hold`(配信を止める。ドライブは最後の目標を保持)か `zero`(速度・効力・Twist を一度ゼロに) |
| `ramp_in_s` | 起動時と停止からの復帰時に、計測値から方策の目標へ線形に混ぜる。速度・効力・Twist は倍率 |
| `joint_limits` | 位置目標を関節ごとに [下限, 上限] にクランプ |
| `max_joint_speed` | 位置目標は 1 周期の変化量を v/rate に、速度目標は ±v に(スカラーか関節ごと) |
| `max_joint_effort` / `max_base_speed` | 効力、Twist の [並進, 回転] をクランプ |

sim2sim のシナリオに `estop_test`(追加の保持中に非常停止を入れて解除)を足し、
「停止中と報告している」「停止中に行動を配信していない」「解除後に再開している」を判定する。
`servo_demo_rl.sim2sim.json` に組み込んだ。実機ではまだ動かしていないので、ここまでが机上と
シミュレーションでの検証になる。

検証: servo_demo_rl(safety: 観測 150 ms、関節 ±1.5 rad、速度 6 rad/s、ランプイン 0.5 s)の sim2sim は
追従に影響なく PASS、非常停止テストも PASS(停止中と報告、停止中の行動配信 0、解除後に再開)。
diffbot_rl(車輪 12 rad/s、停止時ゼロ)も PASS(0.02 / 0.11 / 0.09 m)。
