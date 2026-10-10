# AGENTS.md

Standing agent rules (`UU_AGENT`). Project-specific detail, landmines and recorded
exemptions live in `CLAUDE.md` and `docs/rules-exemptions.md`.

## Project Setup & Git Workflow
* **Project Context:** Check the `/DOCS` root folder for an overall project summary.
* **Git Operations:** Work strictly on the `master` branch unless instructed otherwise. Always commit all pending changes before running a git sync.
* **Scene Construction:** Generate prefabs/objects via MCP or Unity CLI rather than runtime instantiation to enable manual transform adjustments.
* **Unity Performance Safeguards:** Prevent background throttling via MCP or Unity CLI by enabling **No Throttling** (Editor Preferences), **Run In Background** (Player Settings), and continuous auto-ticking.
* **UI Updates:** Document UI changes in an HTML report containing annotated before-and-after screenshots.

## Training Environment & Execution
*(Rule Override: Ignore all MuJoCo, Newton, or Isaac Lab instructions if the project uses `mlagents`.)*
* **Engines & Android Build:** Conduct training in MuJoCo/Newton. Use [https://github.com/joanllobera/mujoco-bin/](https://github.com/joanllobera/mujoco-bin/) for Android builds.
* **Model Pipeline:** Request a skinned mesh prior to training; extract its rig structure and import it into MuJoCo/Newton.
* **Visualizing Training:** Expose the MuJoCo, Isaac Lab, or Newton UI during and after training to monitor creature movement.
* **Resource Optimization:** Close Unity/Unreal during pure RL runs or when training exceeds 30 minutes if it accelerates performance; notify when training ends and editors can reopen.
* Launch TensorBoard immediately on training start, removing any obsolete behaviors first.
* For runs exceeding 30 minutes, generate an HTML report containing screenshots of the 3 most consequential TensorBoard metrics, a performance comparison against prior runs, and explanations across 3 comprehension tiers (Toddler, Child, Adult).
* **Data Logging:** Log all agent actions to file logs or Azure Tables to analyze movement profiles, win/loss patterns, and success vectors.
  Log training data so that it can be aggregated into reports on the underlying meaning of the data over time, to understand how the behaviours and the metrics affect the game results.

## Physics, Biomechanics & Self-Collision
* **Physical Realism:** Enforce Earth gravity, scaled mass distribution, and realistic human joint velocities/forces.
* **Environment Collisions:** Ensure accurate collision bounds; prevent agents from clipping through themselves, other creatures, or the environment.
* **Collider Geometry:** Fit primitive colliders (capsules, boxes, spheres) inside the skinned mesh; never use bones or visual meshes.
* **Exclusions & Limits:** Collide all body-part pairs except direct parent–child links and resting-pose overlaps (mitigated via joint limits).
* **Validation:** Confirm zero self-contact during T-pose, resting stance, and natural arm/leg swings before training.
* **Reward Shaping & Initialization:** Never terminate episodes on self-contact; apply a slight contact-force penalty if exploited. Warm-start new skills from policies trained without self-collision or from a preceding milestone.

## Response Formatting
* Add a 20-word TLDR summary at the end of any response exceeding 100 words.
