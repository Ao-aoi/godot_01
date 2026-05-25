using Godot;
using System;

public partial class CameraController : Camera3D
{
	[Signal]
	public delegate void CrosshairHighlightChangedEventHandler(bool isHighlighted);

	[ExportGroup("Movement Settings")]
	public float MaxMoveSpeed = 8.0f;
	public float MaxWalkSpeed = 3.0f;
	public float Acceleration = 2.0f;
	public float Friction = 8.0f;
	public float Gravity = 15.0f;
	public float JumpForce = 6.0f;

	[ExportGroup("Mouse Settings")]
	public float MouseSensitivity = 0.002f;

	[ExportGroup("Creature Detection")]
	[Export] public float DetectionDistance = 20.0f;
	[Export] public string CreatureGroupName = "creatures";

	[ExportGroup("Tracking Camera")]
	[Export] public Vector3 FollowOffsetLocal = new(0.0f, 1.5f, 2.0f);
	[Export] public float FollowPositionLerpSpeed = 6.0f;
	[Export] public float FollowRotationLerpSpeed = 8.0f;
	[Export] public float MinFollowDistance = 0.5f;
	[Export] public float MaxFollowDistance = 10.0f;
	[Export] public float FollowDistanceStep = 0.5f;

	private bool _isFlying = true;
	private bool _isTracking = false;
	private float _spaceTapTimer = 0.0f;
	private const float DoubleTapDelay = 0.3f;

	private float _rotX = 0.0f;
	private float _rotY = 0.0f;
	private Vector3 _currentVelocity = Vector3.Zero;

	private CharacterBody3D _parentBody;
	private Node3D _trackingTarget;
	private Node _currentHighlighted;

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
			Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
				? Input.MouseModeEnum.Visible
				: Input.MouseModeEnum.Captured;
		}

		if (@event is InputEventMouseButton mouseButton && mouseButton.Pressed)
		{
			if (mouseButton.ButtonIndex == MouseButton.Left && !_isTracking)
			{
				TryZoomInFromCrosshair();
			}
			else if (mouseButton.ButtonIndex == MouseButton.Right && _isTracking)
			{
				ZoomOut();
			}

			// マウスホイールで注目中のカメラをズームイン/ズームアウト
			if (_isTracking)
			{
				if (mouseButton.ButtonIndex == MouseButton.WheelUp)
				{
					AdjustFollowDistance(-FollowDistanceStep);
				}
				else if (mouseButton.ButtonIndex == MouseButton.WheelDown)
				{
					AdjustFollowDistance(FollowDistanceStep);
				}
			}
		}

		if (Input.MouseMode == Input.MouseModeEnum.Captured && !_isTracking && @event is InputEventMouseMotion mouseMotion)
		{
			_rotY -= mouseMotion.Relative.X * MouseSensitivity;
			_rotX -= mouseMotion.Relative.Y * MouseSensitivity;
			_rotX = Mathf.Clamp(_rotX, Mathf.DegToRad(-89f), Mathf.DegToRad(89f));
			GlobalRotation = new Vector3(_rotX, _rotY, 0);
		}

		if (!_isTracking && @event.IsActionPressed("move_up"))
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

		UpdateCenterRayHighlight();
	}

	public override void _PhysicsProcess(double delta)
	{
		// 【修正】MouseModeのチェックを外し、プレイヤーの体が存在するかどうかだけをチェックする
		if (_parentBody == null)
		{
			return;
		}

		// 追跡カメラ作動中は移動させない（必要であれば）
		if (_isTracking)
		{
			UpdateTrackingCamera((float)delta);
			return;
		}

		float fDelta = (float)delta;
		Vector3 horizontalTargetDir = Vector3.Zero;
		Vector3 forward = -GlobalTransform.Basis.Z;
		forward.Y = 0;
		if (forward.Length() > 0) forward = forward.Normalized();

		Vector3 right = GlobalTransform.Basis.X;
		right.Y = 0;
		if (right.Length() > 0) right = right.Normalized();

		// これにより、ショップ画面が開いていてもWASDの入力を受け付けるようになります
		if (Input.IsActionPressed("move_forward")) horizontalTargetDir += forward;
		if (Input.IsActionPressed("move_backward")) horizontalTargetDir -= forward;
		if (Input.IsActionPressed("move_left")) horizontalTargetDir -= right;
		if (Input.IsActionPressed("move_right")) horizontalTargetDir += right;

		if (horizontalTargetDir.Length() > 0)
		{
			horizontalTargetDir = horizontalTargetDir.Normalized();
		}

		if (_isFlying)
		{
			Vector3 verticalTargetDir = Vector3.Zero;
			if (Input.IsActionPressed("move_up")) verticalTargetDir += Vector3.Up;
			if (Input.IsActionPressed("move_down")) verticalTargetDir += Vector3.Down;

			Vector3 targetVec = (horizontalTargetDir * MaxMoveSpeed) + (verticalTargetDir.Normalized() * MaxMoveSpeed);

			if (targetVec.Length() > 0)
				_currentVelocity = _currentVelocity.Lerp(targetVec, Acceleration * fDelta);
			else
				_currentVelocity = _currentVelocity.Lerp(Vector3.Zero, Friction * fDelta);
		}
		else
		{
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

			if (!_parentBody.IsOnFloor())
			{
				_currentVelocity.Y -= Gravity * fDelta;
			}
			else
			{
				_currentVelocity.Y = 0;
				if (Input.IsActionJustPressed("move_up"))
				{
					_currentVelocity.Y = JumpForce;
				}
			}
		}

		_parentBody.Velocity = _currentVelocity;
		_parentBody.MoveAndSlide();
		_currentVelocity = _parentBody.Velocity;
	}

	public void ZoomInToCreature(Node3D target)
	{
		if (target == null || !IsInstanceValid(target))
		{
			return;
		}

		_trackingTarget = target;
		_isTracking = true;
		_parentBody.Velocity = Vector3.Zero;
		_currentVelocity = Vector3.Zero;
		SetHighlightTarget(target);
	}
	
	/// <param name="target">注目する3Dオブジェクト</param>
	public void StartFollow(Node3D target)
	{
		if (target == null || !IsInstanceValid(target))
		{
			return;
		}

		// 既存のズームイン処理（ZoomInToCreature）を活用して追跡を開始
		ZoomInToCreature(target);
		GD.Print($"CameraController: オブジェクト '{target.Name}' の追跡を開始しました。");
	}

	public void ZoomOut()
	{
		_isTracking = false;
		_trackingTarget = null;
		_parentBody.Velocity = Vector3.Zero;
		_currentVelocity = Vector3.Zero;
		Input.MouseMode = Input.MouseModeEnum.Captured;
	}

	private void AdjustFollowDistance(float delta)
	{
		float currentDist = FollowOffsetLocal.Length();
		float newDist = Mathf.Clamp(currentDist + delta, MinFollowDistance, MaxFollowDistance);
		if (Mathf.Abs(newDist - currentDist) < 0.0001f) return;
		if (currentDist > 0.0001f)
		{
			FollowOffsetLocal = FollowOffsetLocal.Normalized() * newDist;
		}
		else
		{
			FollowOffsetLocal = new Vector3(0.0f, FollowOffsetLocal.Y, newDist);
		}
	}

	private void TryZoomInFromCrosshair()
	{
		Node3D target = GetCreatureUnderCrosshair();
		if (target != null)
		{
			ZoomInToCreature(target);
		}
	}

	private void UpdateTrackingCamera(float delta)
	{
		if (_trackingTarget == null || !IsInstanceValid(_trackingTarget))
		{
			ZoomOut();
			return;
		}

		Vector3 worldOffset = new Vector3(
			FollowOffsetLocal.X, 
			FollowOffsetLocal.Y, 
			FollowOffsetLocal.Z
		);

		// もし「常にクリーチャーの初期の背後に回り込ませたい」場合は、
		// クリーチャーの「現在の位置」から、転がりを無視した「Y軸回転（方位）だけ」を抽出してオフセットを計算します。
		Vector3 targetForward = -_trackingTarget.GlobalTransform.Basis.Z;
		targetForward.Y = 0.0f; // 垂直方向の傾きを捨てる
		if (targetForward.Length() < 0.001f)
		{
			targetForward = Vector3.Forward; // 真下や真上を向いていた時のセーフティ
		}
		targetForward = targetForward.Normalized();
		
		// Y軸回転だけのBasisを即席で作る
		Basis flatBasis = Basis.LookingAt(targetForward, Vector3.Up);
		Vector3 desiredPosition = _trackingTarget.GlobalPosition + (flatBasis * FollowOffsetLocal);

		// 2. レイキャストによる地面・障害物のめり込みチェック (既存のロジックを維持)
		if (IsInsideTree() && GetWorld3D() != null)
		{
			Vector3 rayOrigin = _trackingTarget.GlobalPosition + Vector3.Up * 0.3f; // 注視点を少し下げるか調整
			Vector3 rayTarget = desiredPosition;

			PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayTarget);
			query.CollideWithAreas = false;
			query.CollideWithBodies = true;
			
			var excludeArray = new Godot.Collections.Array<Rid>();
			if (_parentBody != null) excludeArray.Add(_parentBody.GetRid());
			if (_trackingTarget is CollisionObject3D co) excludeArray.Add(co.GetRid());
			query.Exclude = excludeArray;

			var spaceState = GetWorld3D().DirectSpaceState;
			var hit = spaceState.IntersectRay(query);

			if (hit.Count > 0)
			{
				Vector3 hitPos = hit["position"].As<Vector3>();
				Vector3 hitNormal = hit["normal"].As<Vector3>();
				desiredPosition = hitPos + hitNormal * 0.2f;
			}
		}

		// 3. 位置の追従（Lerp）
		GlobalPosition = GlobalPosition.Lerp(desiredPosition, FollowPositionLerpSpeed * delta);

		// 4. 【一流のカメラワーク】視線の回転処理
		// クリーチャーの「中心（ピボット）」ではなく、やや上（頭のあたりなど）を常に捉える
		Vector3 lookAtTarget = _trackingTarget.GlobalPosition + Vector3.Up * 0.2f; 
		Vector3 toTarget = (lookAtTarget - GlobalPosition).Normalized();

		if (toTarget.LengthSquared() > 0.0001f)
		{
			// 第2引数を Vector3.Up にすることで、カメラの「傾き（ロール）」を完全に封殺します
			Basis desiredBasis = Basis.LookingAt(toTarget, Vector3.Up);
			Quaternion currentQ = GlobalTransform.Basis.GetRotationQuaternion();
			Quaternion targetQ = desiredBasis.GetRotationQuaternion();
			
			// じわっと滑らかにターゲットをフレームに収める
			Quaternion newQ = currentQ.Slerp(targetQ, FollowRotationLerpSpeed * delta);
			GlobalTransform = new Transform3D(new Basis(newQ), GlobalPosition);

			// 次回自由カメラに戻った時のための角度の同期
			Vector3 euler = GlobalRotation;
			_rotX = euler.X;
			_rotY = euler.Y;
		}
	}
	private void UpdateCenterRayHighlight()
	{
		Node3D creature = GetCreatureUnderCrosshair();
		SetHighlightTarget(creature);
	}

	private Node3D GetCreatureUnderCrosshair()
	{
		if (!IsInsideTree() || GetWorld3D() == null)
		{
			return null;
		}

		Vector3 from = GlobalTransform.Origin;
		Vector3 to = from + (-GlobalTransform.Basis.Z * DetectionDistance);
		PhysicsRayQueryParameters3D query = PhysicsRayQueryParameters3D.Create(from, to);
		query.CollideWithAreas = true;
		query.CollideWithBodies = true;
		if (_parentBody != null)
		{
			query.Exclude = new Godot.Collections.Array<Rid> { _parentBody.GetRid() };
		}

		var spaceState = GetWorld3D().DirectSpaceState;
		var hit = spaceState.IntersectRay(query);
		if (hit.Count == 0)
		{
			return null;
		}

		Node collider = hit["collider"].As<Node>();
		return ResolveCreatureNode(collider);
	}

	private Node3D ResolveCreatureNode(Node collider)
	{
		Node current = collider;
		while (current != null)
		{
			if (current is Node3D node3d &&
				(current.IsInGroup(CreatureGroupName) || current.HasMethod("ShowHighlight")))
			{
				return node3d;
			}
			current = current.GetParent();
		}

		return null;
	}

	private void SetHighlightTarget(Node newTarget)
	{
		if (_currentHighlighted == newTarget)
		{
			return;
		}

		if (_currentHighlighted != null && IsInstanceValid(_currentHighlighted) && _currentHighlighted.HasMethod("ShowHighlight"))
		{
			_currentHighlighted.Call("ShowHighlight", false);
		}

		_currentHighlighted = newTarget;

		if (_currentHighlighted != null && IsInstanceValid(_currentHighlighted) && _currentHighlighted.HasMethod("ShowHighlight"))
		{
			_currentHighlighted.Call("ShowHighlight", true);
		}

		EmitSignal(SignalName.CrosshairHighlightChanged, _currentHighlighted != null);
	}
}
