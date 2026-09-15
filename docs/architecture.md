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

やること(シミュレータ側リポジトリで):

- `Assets/Scripts/{ServoModel,SdfWorld,UrdfProperties,Aerodynamics,Hydrodynamics}` の
  asmdef 単位を `Packages/` 配下の UPM パッケージに移し、`package.json` を付ける
- `Assets/Scripts/` 直下の `JointStatePub` / `JointStateSub` / `PhysicsRateController` /
  `SimulationControl` 系も core パッケージにまとめる
- 依存(NaughtyWaterBuoyancy など)は各 `package.json` の dependencies に書く

それまでは `unity/UniRoboLab/Packages/manifest.json` には既にパッケージ化済みの
依存だけを書いておく。

## 5. 名前と商標

- Unity の商標ガイドラインは製品名・プロジェクト名に「Unity」を含めることを
  認めていない。公開・商用化時に改名を迫られると ID・名前空間の総書き換えになる。
- そのため名前に "Unity" の文字列を入れない。"Uni" は universal / university とも
  読めるが、否定的な意味はなく、暗喩は README の一行目で補う。
- `unirobolab` は 2026-09-15 時点で GitHub の名前一致 0 件、PyPI 空き。
- 表記は UniRoboLab(Uni / Robo / Lab に切れる)。

## 6. マイルストーン

- **M0** リポジトリ雛形(このコミット)
- **M1** 既存の ONNX ポリシー + 契約を入力に ROS 2 パッケージを生成し、
  Unity で sim2sim して合否を出す。**学習 GUI より先に作る。**
  差別化の核がここなので、最初に検証する
- **M2** 学習 GUI。Unity 複数インスタンスのベクトル化環境ラッパー、
  手動ステップ(`Physics.Simulate`)と決定論性、`uv` 同梱の学習器、
  学習曲線と報酬項ごとの寄与の表示、sim2sim 失敗時の原因診断
- **M3** 実機デプロイ。まずは自前シミュレータ + 実機 1 構成に絞る
  (ros2_control のインターフェースは種類が多く、保守負担が膨らむ)

最初のタスクは低自由度に絞る: アーム到達、移動ロボット走行、SG90 級の小型機。
四足歩行の大規模学習は外部バックエンドに任せる。
