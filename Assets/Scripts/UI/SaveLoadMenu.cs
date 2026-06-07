using UnityEngine;
using UnityEngine.UIElements;
using System;
using System.IO;

/// <summary>
/// Component that applies actions from the save-load menu UI to the hex map.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class SaveLoadMenu : MonoBehaviour
{
	const int mapFileVersion = 5;

	[SerializeField]
	HexGrid hexGrid;

	bool saveMode;

	TextField nameField;

	ListView mapList;

	string[] mapNames;

	public void Open(bool saveMode)
	{
		this.saveMode = saveMode;
		gameObject.SetActive(true);
		HexMapCamera.Locked = true;

		VisualElement root = GetComponent<UIDocument>().rootVisualElement;

		root.Q<Label>("MenuLabel").text = saveMode ? "Save Map" : "Load Map";

		var actionButton = root.Q<Button>("ActionButton");
		actionButton.text = saveMode ? "Save" : "Load";
		actionButton.clicked += Action;

		root.Q<Button>("DeleteButton").clicked += Delete;
		root.Q<Button>("CancelButton").clicked += Close;

		nameField = root.Q<TextField>("NameField");

		mapList = root.Q<ListView>("MapList");
		mapList.makeItem = static () => new Label();
		mapList.bindItem = (e, i) => ((Label)e).text = mapNames[i];
		mapList.selectedIndicesChanged += (indices) =>
			nameField.value = mapNames[mapList.selectedIndex];

		FillList();
	}

	public void Close()
	{
		gameObject.SetActive(false);
		HexMapCamera.Locked = false;
	}

	public void Action()
	{
		string path = GetSelectedPath();
		if (path == null)
		{
			return;
		}
		if (saveMode)
		{
			Save(path);
		}
		else
		{
			Load(path);
		}
		Close();
	}

	public void Delete()
	{
		string path = GetSelectedPath();
		if (path == null)
		{
			return;
		}
		if (File.Exists(path))
		{
			File.Delete(path);
		}
		nameField.value = "";
		FillList();
	}

	void FillList()
	{
		mapNames = Directory.GetFiles(Application.persistentDataPath, "*.map");
		for (int i = 0; i < mapNames.Length; i++)
		{
			mapNames[i] = Path.GetFileNameWithoutExtension(mapNames[i]);
		}
		Array.Sort(mapNames);
		mapList.itemsSource = mapNames;
	}

	string GetSelectedPath()
	{
		string mapName = nameField.value;
		if (mapName.Length == 0)
		{
			return null;
		}
		return Path.Combine(Application.persistentDataPath, mapName + ".map");
	}

	void Save (string path)
	{
		using var writer = new BinaryWriter(File.Open(path, FileMode.Create));
		writer.Write(mapFileVersion);
		hexGrid.Save(writer);
	}

	void Load(string path)
	{
		if (!File.Exists(path))
		{
			Debug.LogError("File does not exist " + path);
			return;
		}
		using var reader = new BinaryReader(File.OpenRead(path));
		int header = reader.ReadInt32();
		if (header <= mapFileVersion)
		{
			hexGrid.Load(reader, header);
			HexMapCamera.ValidatePosition();
		}
		else
		{
			Debug.LogWarning("Unknown map format " + header);
		}
	}
}
