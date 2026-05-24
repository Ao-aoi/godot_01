using Godot;
using System;

public partial class CameraController : Camera3D
{
	[ExportGroup("Movement Settings")]
	public float MaxMoveSpeed = 8.0f;
	public float MaxWalkSpeed = 3.0f;
	public float Acceleration = 2.0f;      // 飛行モードの加速
	public float Friction = 8.0f;          // 飛行モードの減速（滑る強さ）
	public float Gravity = 15.0f;          // 落下時の重力の強さ
	public float JumpForce = 6.0f;          // 歩行時のジャンプ力

	[ExportGroup("Mouse Settings")]
	public float MouseSensitivity = 0.002f;

	// 飛行モードのフラグ (マイクラのフライト状態)
	private bool _isFlying = true;

	// スペースキーのダブルタップ判定用変数
	private float _spaceTapTimer = 0.0f;
	private const float DoubleTapDelay = 0.3f; 

	private float _rotX = 0.0f;
	private float _rotY = 0.0f;
	private Vector3 _currentVelocity = Vector3.Zero;

	private CharacterBody3D _parentBody;

	public override void _Ready()
	{
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_rotY = GlobalRotation.Y;
		_rotX = GlobalRotation.X;

		_parentBody = GetParent() as CharacterBody3D;
		if (_parentBody == null)
		{
			GD.PrintErr("エラー：Camera3Dの親ノードを『CharacterBody3D』にしてください！");
		}
	}

	public override void _Input(InputEvent @event)
	{
		if (@event.IsActionPressed("ui_cancel"))
		{
			Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured ? 
				Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
		}

		// マウスによる視点移動
		if (Input.MouseMode == Input.MouseModeEnum.Captured && @event is InputEventMouseMotion mouseMotion)
		{
			_rotY -= mouseMotion.Relative.X * MouseSensitivity;
			_rotX -= mouseMotion.Relative.Y * MouseSensitivity;
			_rotX = Mathf.Clamp(_rotX, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
			GlobalRotation = new Vector3(_rotX, _rotY, 0);
		}

		// スペースキーの2回押し（ダブルタップ）検知で飛行切替
		if (@event.IsActionPressed("move_up"))
		{
			if (_spaceTapTimer > 0.0f)
			{
				_isFlying = !_isFlying;
				_currentVelocity = Vector3.Zero; 
				GD.Print(_isFlying ? "飛行モード：ON" : "飛行モード：OFF（落下します）");
				_spaceTapTimer = 0.0f; 
			}
			else
			{
				_spaceTapTimer = DoubleTapDelay;
			}
		}
	}

	public override void _Process(double delta)
	{
		if (_spaceTapTimer > 0.0f)
		{
			_spaceTapTimer -= (float)delta;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (Input.MouseMode != Input.MouseModeEnum.Captured || _parentBody == null) return;

		float fDelta = (float)delta;
		Vector3 targetVelocity = Vector3.Zero;

		// 1. 水平方向（WASD）の入力方向ベクトル計算
		Vector3 horizontalTargetDir = Vector3.Zero;
		Vector3 forward = -GlobalTransform.Basis.Z;
		forward.Y = 0;
		if (forward.Length() > 0) forward = forward.Normalized();

		Vector3 right = GlobalTransform.Basis.X;
		right.Y = 0;
		if (right.Length() > 0) right = right.Normalized();

		if (Input.IsActionPressed("move_forward"))  horizontalTargetDir += forward;
		if (Input.IsActionPressed("move_backward")) horizontalTargetDir -= forward;
		if (Input.IsActionPressed("move_left"))     horizontalTargetDir -= right;
		if (Input.IsActionPressed("move_right"))    horizontalTargetDir += right;

		if (horizontalTargetDir.Length() > 0)
		{
			horizontalTargetDir = horizontalTargetDir.Normalized();
		}

		// 2. モード別の移動ロジック
		if (_isFlying)
		{
			// 【🛸 飛行モード：心地よい慣性移動】
			Vector3 verticalTargetDir = Vector3.Zero;
			if (Input.IsActionPressed("move_up"))   verticalTargetDir += Vector3.Up;
			if (Input.IsActionPressed("move_down")) verticalTargetDir += Vector3.Down;

			// 水平と垂直の目標速度を合成
			Vector3 targetVec = (horizontalTargetDir * MaxMoveSpeed) + (verticalTargetDir.Normalized() * MaxMoveSpeed);

			// 目標速度に向かって滑らかに補間（スーッと滑る）
			if (targetVec.Length() > 0)
				_currentVelocity = _currentVelocity.Lerp(targetVec, Acceleration * fDelta);
			else
				_currentVelocity = _currentVelocity.Lerp(Vector3.Zero, Friction * fDelta);
		}
		else
		{
			// 【🚶 歩行モード：慣性なしのキビキビ移動 ＆ 重力】
			
			// 入力がある時は最大速度、ない時はピタッと0にする（慣性を完全に排除）
			if (horizontalTargetDir.Length() > 0)
			{
				_currentVelocity.X = horizontalTargetDir.X * MaxWalkSpeed;
				_currentVelocity.Z = horizontalTargetDir.Z * MaxWalkSpeed;
			}
			else
			{
				_currentVelocity.X = 0;
				_currentVelocity.Z = 0;
			}

			// 垂直方向：地面についていないなら重力落下
			if (!_parentBody.IsOnFloor())
			{
				_currentVelocity.Y -= Gravity * fDelta;
			}
			else
			{
				_currentVelocity.Y = 0;

				// 地面にいる時にスペースを「1回」押したらジャンプ
				if (Input.IsActionJustPressed("move_up"))
				{
					_currentVelocity.Y = JumpForce; // ジャンプ力
				}
			}
		}

		// 3. 速度を親に適用して動かす
		_parentBody.Velocity = _currentVelocity;
		_parentBody.MoveAndSlide();

		// 物理演算の衝突結果（壁衝突など）を速度にフィードバック
		_currentVelocity = _parentBody.Velocity;
	}
}
