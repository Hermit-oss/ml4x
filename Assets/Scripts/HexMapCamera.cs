using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Component that controls the singleton camera that navigates the hex map.
/// </summary>
public class HexMapCamera : MonoBehaviour
{
	[SerializeField]
	float stickMinZoom, stickMaxZoom;

	[SerializeField]
	float swivelMinZoom, swivelMaxZoom;

	[SerializeField]
	float moveSpeedMinZoom, moveSpeedMaxZoom;

	[SerializeField]
	float rotationSpeed;

	[SerializeField]
	HexGrid grid;

	Transform swivel, stick;

	float zoom = 1f;

	float rotationAngle;

	InputAction moveAction, rotateAction, zoomAction;

	static HexMapCamera instance;

	/// <summary>
	/// Whether the singleton camera controls are locked.
	/// </summary>
	public static bool Locked
	{
		set => instance.enabled = !value;
	}

	/// <summary>
	/// Validate the position of the singleton camera.
	/// </summary>
	public static void ValidatePosition() => instance.AdjustPosition(0f, 0f);

	void Awake()
	{
		swivel = transform.GetChild(0);
		stick = swivel.GetChild(0);
		moveAction = InputSystem.actions.FindAction("Move");
		moveAction.actionMap.Enable();
		rotateAction = InputSystem.actions.FindAction("Rotate");
		zoomAction = InputSystem.actions.FindAction("Zoom");
	}

	void OnEnable()
	{
		instance = this;
		ValidatePosition();
	}

	void Update()
	{
		float zoomDelta = zoomAction.ReadValue<Vector2>().y;
		if (zoomDelta != 0f)
		{
			AdjustZoom(zoomDelta);
		}

		float rotationDelta = rotateAction.ReadValue<float>();
		if (rotationDelta != 0f)
		{
			AdjustRotation(rotationDelta);
		}

		var moveDelta = moveAction.ReadValue<Vector2>();
		if (moveDelta.x != 0f || moveDelta.y != 0f)
		{
			AdjustPosition(moveDelta.x, moveDelta.y);
		}
	}

	void AdjustZoom(float delta)
	{
		zoom = Mathf.Clamp01(zoom + delta);

		float distance = Mathf.Lerp(stickMinZoom, stickMaxZoom, zoom);
		stick.localPosition = new Vector3(0f, 0f, distance);

		float angle = Mathf.Lerp(swivelMinZoom, swivelMaxZoom, zoom);
		swivel.localRotation = Quaternion.Euler(angle, 0f, 0f);
	}

	void AdjustRotation (float delta)
	{
		rotationAngle += delta * rotationSpeed * Time.deltaTime;
		if (rotationAngle < 0f)
		{
			rotationAngle += 360f;
		}
		else if (rotationAngle >= 360f)
		{
			rotationAngle -= 360f;
		}
		transform.localRotation = Quaternion.Euler(0f, rotationAngle, 0f);
	}

	void AdjustPosition(float xDelta, float zDelta)
	{
		Vector3 direction =
			transform.localRotation *
			new Vector3(xDelta, 0f, zDelta).normalized;
		float damping = Mathf.Max(Mathf.Abs(xDelta), Mathf.Abs(zDelta));
		float distance =
			Mathf.Lerp(moveSpeedMinZoom, moveSpeedMaxZoom, zoom) *
			damping * Time.deltaTime;

		Vector3 position = transform.localPosition;
		position += direction * distance;
		transform.localPosition =
			grid.Wrapping ? WrapPosition(position) : ClampPosition(position);
	}

	Vector3 ClampPosition(Vector3 position)
	{
		float xMax = (grid.CellCountX - 0.5f) * HexMetrics.innerDiameter;
		position.x = Mathf.Clamp(position.x, 0f, xMax);

		float zMax = (grid.CellCountZ - 1) * (1.5f * HexMetrics.outerRadius);
		position.z = Mathf.Clamp(position.z, 0f, zMax);

		return position;
	}

	Vector3 WrapPosition(Vector3 position)
	{
		float width = grid.CellCountX * HexMetrics.innerDiameter;
		while (position.x < 0f)
		{
			position.x += width;
		}
		while (position.x > width)
		{
			position.x -= width;
		}

		float zMax = (grid.CellCountZ - 1) * (1.5f * HexMetrics.outerRadius);
		position.z = Mathf.Clamp(position.z, 0f, zMax);

		grid.CenterMap(position.x);
		return position;
	}
}
