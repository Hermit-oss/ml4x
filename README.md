# Hex Map RL Project

A turn-based 4X-style hex-map strategy game with reinforcement-learning agents,
built in Unity 2022 LTS with ML-Agents 1.1.0. Agents learn three scenarios via
curriculum learning: solo economy (`Solo_1v0`), 1v1 combat (`Combat_1v1`), and
2v2 multi-unit cooperation (`Combat_2v2`).

## Quick start (run a trained model)

1. Install **Unity Hub** + **Unity Editor 2022 LTS**.
2. Clone this repository and open the project root in Unity Hub.
3. Open `Assets/Scenes/Hex Map Scene.unity`.
4. On the `Unit` prefab, set `BehaviorParameters > Behavior Type` to
   `InferenceOnly` and drag one of the trained models into the `Model` field:
   - `Assets/Scripts/ML/results/soloIII/HexUnit.onnx` — solo economy
   - `Assets/Scripts/ML/results/combatIII/HexUnit.onnx` — 1v1 combat
   - `Assets/Scripts/ML/results/combat2v2_II/HexUnit.onnx` — 2v2 combat
5. On the `TurnManager` GameObject, set `HexMLEnvironment > Scenario` to match
   the model you loaded.
6. Press **Play**.

## Training a new model

Python 3.10 with the dependencies in `requirements.txt`:

```bash
python -m venv ml_venv
ml_venv\Scripts\activate          # Windows
# source ml_venv/bin/activate     # Linux / macOS
pip install --upgrade pip
pip install -r requirements.txt
```

Then launch training and press Play in Unity when the trainer prints
`Listening on port 5004`:

```bash
mlagents-learn Assets/Scripts/ML/config.yaml --run-id=<your_id>
# add --force to overwrite an existing run
# add --resume to continue from a previous checkpoint with the same id
# add --initialize-from=<other_id> to warm-start weights from another run
```

Monitor with TensorBoard:

```bash
tensorboard --logdir Assets/Scripts/ML/results --port 6006
```

## Repository contents

| Path | Purpose |
|---|---|
| `Assets/` | All Unity assets (scripts, prefabs, scenes, materials). |
| `Assets/Scripts/` | C# source for the game engine and ML-Agents integration. |
| `Assets/Scripts/ML/config.yaml` | PPO + ICM training configuration. |
| `Assets/Scripts/ML/results/<id>/HexUnit.onnx` | One trained model per scenario. |
| `Packages/` | Unity Package Manager manifest. |
| `ProjectSettings/` | Project configuration. |
| `requirements.txt` | Python dependencies for the trainer. |

Unity-generated folders (`Library/`, `Temp/`, `Logs/`, `obj/`, `Build/`,
`UserSettings/`, `.vs/`) and Python virtualenvs (`ml_venv/`, `venv/`) are in
`.gitignore` and **not** committed — they are regenerated locally.
