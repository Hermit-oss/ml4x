using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Component that applies actions from the new map menu UI to the hex map.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class NewMapMenu : MonoBehaviour
{
	[SerializeField]
	HexGrid hexGrid;

	[SerializeField]
	HexMapGenerator mapGenerator;

	bool generateMaps = true;

	bool wrapping = true;

	public void Open()
	{
		gameObject.SetActive(true);
		HexMapCamera.Locked = true;

		VisualElement root = GetComponent<UIDocument>().rootVisualElement;
		
		var generateToggle = root.Q<Toggle>("Generate");
		generateToggle.value = generateMaps;
		generateToggle.RegisterValueChangedCallback(
			change => generateMaps = change.newValue);

		var wrappingToggle = root.Q<Toggle>("Wrapping");
		wrappingToggle.value = wrapping;
		wrappingToggle.RegisterValueChangedCallback(
			change => wrapping = change.newValue);
		
		root.Q<Button>("Small").clicked += () => CreateMap(20, 15);
		root.Q<Button>("Medium").clicked += () => CreateMap(40, 30);
		root.Q<Button>("Large").clicked += () => CreateMap(80, 60);
		root.Q<Button>("Cancel").clicked += Close;
	}

	public void Close()
	{
		gameObject.SetActive(false);
		HexMapCamera.Locked = false;
	}

	void CreateMap(int x, int z)
	{
		if (generateMaps)
		{
			mapGenerator.GenerateMap(x, z, wrapping);
		}
		else
		{
			hexGrid.CreateMap(x, z, wrapping);
		}
		HexMapCamera.ValidatePosition();
		Close();
	}
}
