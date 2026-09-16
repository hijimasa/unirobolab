# Unity 6 と強化学習向け GPU 物理: 調査メモ(2026-09-16)

unirobolab の設計判断のための調査。Web 上の一次情報(Unity マニュアル・changelog・フォーラムの
Unity スタッフ回答・各エンジンの公式資料)に基づく。出典は末尾。

## 結論

- **Unity 6 に GPU 物理は無い。** GameObject 物理は CPU 版 PhysX 4.x のまま。ECS 用の Unity Physics /
  Havok も CPU(Burst/Jobs)。PhysX 5 の GPU rigid body / GPU articulation は Unity から使えない。
  Unity 6.1 で Physics 設定に「GameObject SDK」の選択肢が増えたが PhysX のみで、PhysX 5 への
  差し替えは 2025 GDC のロードマップで言及された程度。
- Unity 内で並列度を上げる手段は CPU スケーリングだけ: 1 プロセス内の複数エリア、複数 PhysicsScene、
  複数プロセス。`PhysicsScene.Simulate` はメインスレッド専用でジョブ化できず、複数シーンも逐次。
  したがって **プロセス並列(unirobolab の sim_pool)が Unity で取れる最大の並列**。
- 数千〜数万環境の GPU バッチ物理が要るなら Newton / MuJoCo Warp / Isaac Lab / Genesis 側で回し、
  Unity はレンダリング・シーン作成・ROS 2 配備・実機と同じ構成での検証を担う分業が現実的。

## 各項目

### 物理エンジン
- GameObject 物理は PhysX 4.x(`Physics.GetCurrentIntegrationInfo()`、6000.0.50f1 で追加、での確認報告)。
- Unity Physics(ECS)は 6.5.0(2026-01)からコアパッケージ。剛体・ジョイント上限の大幅引き上げ、
  並列制約ソルバ、6.5.0 の Direct Solver(高い質量比・剛性比、長い関節列、ループ機構、ロボティクス明記)。
  ただし **Featherstone(縮約座標)の ArticulationBody 相当は無く**、ジョイントは最大座標の拘束+モータ。
  `PhysicsWorldIndex` で 1 プロセス内に独立ワールドを複数持てる。GPU 化の記述は changelog に無い。
- 有志が PhysX 5.6 をネイティブ DLL で Unity に持ち込んだ事例はあるが、ArticulationBody 非互換の実験段階。

### 手動ステップと複数シーン
- `SimulationMode.Script` + `Physics.Simulate(dt)`。FixedUpdate は発火しない(本体の
  ServoJointModel などは FixedUpdate 依存なので、unirobolab は `captureDeltaTime` 方式を採った)。
- `SceneManager.CreateScene(..., LocalPhysicsMode.Physics3D)` で独立 PhysicsScene。各シーンは
  `GetPhysicsScene().Simulate(dt)` で個別にステップ。ジョブから呼べず、シーン間は逐次
  (Unity スタッフ MelvMay の回答)。内部の PhysX ワーカーは使われる。

### ML-Agents 4.x
- 4.0.0(2025-08)で Unity 6000.0 以上、Inference Engine 対応。並列化は従来どおり
  `TrainingAreaReplicator`(1 プロセス内のエリア複製)と `--num-envs`(複数プロセス)。
  gRPC でステップごとに往復するので、環境数を増やしても往復レイテンシが支配的になりやすい。

### Inference Engine(旧 Sentis)
- CPU / GPUCompute / GPUPixel。ML-Agents 公式は「MLP 程度なら Burst(CPU)推論の方が GPU より速い。
  GPU は視覚エンコーダ向け」と明記。Unity 側でポリシーを持つ構成は配備・評価には有効だが、
  学習の高速化要因にはならない。

### GPU バッチ物理エンジンと Unity の立ち位置
- Newton(NVIDIA/DeepMind/Disney、Warp、OpenUSD、微分可能)、Isaac Lab 3.0 の Newton 統合
  (MuJoCo Warp、CUDA graph)、Genesis(Taichi/Quadrants)。いずれも Unity・ROS 連携は本体には無い。
- MuJoCo Unity plugin(公式): MJCF を Unity に取り込み `mj_step` を FixedUpdate で呼ぶ。
  「外部プロセスが物理を駆動し、Unity は描画」を公式に想定。CPU 版のみ。
  「Unity から外部物理を使う」最も成熟した事例。
- 総説(NVIDIA "State of Simulation for Physical AI")では学習側は Isaac Lab / MuJoCo Warp / Newton が標準。

### RL に効く周辺
- `-batchmode -nographics` でフレームレート無制限だが、`vSyncCount != 0` だと 60 fps に張り付く。
  `vSyncCount = 0` と `targetFrameRate = -1` で最速。1 フレームの下限はエンジン更新ループ分。
- `Time.captureDeltaTime` は実時間に関係なく固定 dt で進める(ML-Agents の `--capture-frame-rate`)。
- `Physics.*` / `ArticulationBody` はメインスレッド専用。多数ロボットは `GetJointPositions(List)` 等で
  まとめて取って NativeArray にコピーし、後処理だけ Burst 化。`Physics.autoSyncTransforms = false`。

## unirobolab への含意

- 今の方針(直結サーバ + 1 シーン K 体 + プロセス並列)は Unity で取れる上限に沿っている。
  GPU 物理を待つ・自作するのは不可。
- ロボット記述(URDF/MJCF/USD)を Unity と GPU シミュレータの間で共有するアセットパイプラインが
  あれば、学習は Isaac Lab / Newton、検証・配備は Unity という分業が成立する。契約(contract)は
  この分業でも単一情報源として機能する。
- 物理律速のロボット(diffbot)では、観測・報酬の Burst 化や不要なトピック配信の停止で
  フレーム側の費用を削る余地がある。

## 出典
- Unity 6.3 マニュアル Built-in 3D physics: https://docs.unity3d.com/6000.3/Documentation/Manual/PhysicsOverview.html
- Physics project settings (GameObject SDK): https://docs.unity3d.com/Manual/class-PhysicsManager.html
- "How can I choose another physics engine?": https://discussions.unity.com/t/how-can-i-choose-another-physics-engine/1635786
- "PhysX future?": https://discussions.unity.com/t/physx-future/1709078
- Unity 6000.0.50f1: https://unity.com/releases/editor/whats-new/6000.0.50f1
- PhysX 5.4 GPU Simulation: https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/GPURigidBodies.html
- PhysX 5.6 を Unity に統合した事例: https://dev.to/spicytech/integrating-nvidia-physx-56-with-unity-engine-a-journey-into-deformable-gpu-physics-4lgf
- Unity Physics 6.5 changelog: https://docs.unity3d.com/Packages/com.unity.physics@6.5/changelog/CHANGELOG.html
- Unity Physics 6.6 changelog: https://docs.unity3d.com/Packages/com.unity.physics@6.6/changelog/CHANGELOG.html
- Unity Physics 複数ワールド: https://docs.unity3d.com/Packages/com.unity.physics@1.2/manual/group-body.html
- Havok Physics for Unity: https://docs.unity3d.com/6000.1/Documentation/Manual/com.havok.physics.html
- Articulations: https://docs.unity3d.com/6000.4/Documentation/Manual/physics-articulations.html
- 手動物理シミュレーション: https://docs.unity3d.com/6000.3/Documentation/Manual/physics-optimization-cpu-manual-simulation.html
- Physics.Simulate: https://docs.unity3d.com/ScriptReference/Physics.Simulate.html
- PhysicsScene.Simulate: https://docs.unity3d.com/ScriptReference/PhysicsScene.Simulate.html
- Multi-scene physics: https://docs.unity3d.com/Manual/physics-multi-scene.html
- "Calling PhysicsScene.Simulate in Job": https://discussions.unity.com/t/calling-physicsscene-simulate-in-job/932129
- "Are Physics Scenes multithreaded?": https://discussions.unity.com/t/are-physics-scenes-multithreaded/910565
- ML-Agents 4.0 changelog: https://docs.unity3d.com/Packages/com.unity.ml-agents@4.0/changelog/CHANGELOG.html
- ML-Agents Inference Engine: https://docs.unity3d.com/Packages/com.unity.ml-agents@4.0/manual/Inference-Engine.html
- ML-Agents Training: https://unity-technologies.github.io/ml-agents/Training-ML-Agents/
- num-envs vs 複数エリア: https://github.com/Unity-Technologies/ml-agents/issues/2298
- Inference Engine 概要: https://docs.unity3d.com/Packages/com.unity.ai.inference@2.6/manual/index.html
- Newton: https://github.com/newton-physics/newton / https://developer.nvidia.com/newton-physics
- Isaac Lab Newton 統合: https://isaac-sim.github.io/IsaacLab/main/source/experimental-features/newton-physics-integration/index.html
- NVIDIA "State of Simulation for Physical AI": https://huggingface.co/blog/nvidia/state-of-simulation-for-physical-ai
- Genesis: https://github.com/Genesis-Embodied-AI/genesis-world
- MuJoCo Unity plugin: https://mujoco.readthedocs.io/en/latest/unity.html
- Time.captureDeltaTime: https://docs.unity3d.com/ScriptReference/Time-captureDeltaTime.html
- batchmode/nographics の FPS 上限: https://discussions.unity.com/t/batchmode-nographics-with-fps-limit/196081
- Job system 概要: https://docs.unity3d.com/6000.3/Documentation/Manual/job-system-overview.html
