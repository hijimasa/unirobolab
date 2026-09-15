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

### 発見: joint_states が 100 ms ごとにまとめて届く

`/ServoDemo/joint_states` の到着間隔は p50 0.3 ms、p95 100 ms で、約 3 通が 100 ms ごとに
バーストで届く(シミュレータ側 `JointStatePub.publishRateHz` の既定は 30 Hz)。
ROS-TCP-Connector / Endpoint のどこかで 100 ms 単位に束ねられている。
50 Hz のポリシーは最大 100 ms 古い観測で動くことになる。

- sim2sim ではシナリオの `max_obs_age_s` で許容量を明示する(servo_demo は 0.12)。
- M2 の学習環境はこの遅延を入れるか、シミュレータ側の輸送を直す必要がある。
  本体リポジトリ側の調査項目として残す(閉ループ制御の忠実度に関わる)。
