using UnityEngine;
using System.Collections.Generic;

public class HutUpgradable : MonoBehaviour
{
    [Header("Current Level")]
    public int level = 1;   // 1 = base farmhouse

    [Header("Level Models (leave element 0 EMPTY = base farmhouse)")]
    public GameObject[] levelModels;

    [Header("Planet-Specific MAX LEVEL Models")]
    [Tooltip("Used INSTEAD of the last entry in Level Models above, once this hut reaches max level — one model per planet, so the max-level hut looks different depending on where you are. Every OTHER level uses the same model regardless of planet. Leave any of these empty to just fall back to the generic max-level model from Level Models for that planet.")]
    public GameObject earthMaxLevelModel;
    public GameObject moonMaxLevelModel;
    public GameObject venusMaxLevelModel;
    public GameObject marsMaxLevelModel;
    public GameObject plutoMaxLevelModel;

    private RabbitHut hut;
    private readonly List<Renderer> baseRenderers = new List<Renderer>();

    void Awake()
    {
        hut = GetComponent<RabbitHut>();

        Renderer[] all = GetComponentsInChildren<Renderer>(true);
        foreach (Renderer r in all)
            if (!IsUnderLevelModel(r.transform))
                baseRenderers.Add(r);
    }

    bool IsUnderLevelModel(Transform t)
    {
        if (levelModels != null)
        {
            foreach (GameObject m in levelModels)
            {
                if (m == null) continue;
                if (t == m.transform || t.IsChildOf(m.transform)) return true;
            }
        }

        foreach (GameObject m in AllMaxLevelModels())
        {
            if (m == null) continue;
            if (t == m.transform || t.IsChildOf(m.transform)) return true;
        }

        return false;
    }

    IEnumerable<GameObject> AllMaxLevelModels()
    {
        yield return earthMaxLevelModel;
        yield return moonMaxLevelModel;
        yield return venusMaxLevelModel;
        yield return marsMaxLevelModel;
        yield return plutoMaxLevelModel;
    }

    GameObject MaxLevelModelForPlanet(PlanetManager.Planet planet)
    {
        GameObject model = planet switch
        {
            PlanetManager.Planet.Earth => earthMaxLevelModel,
            PlanetManager.Planet.Moon => moonMaxLevelModel,
            PlanetManager.Planet.Venus => venusMaxLevelModel,
            PlanetManager.Planet.Mars => marsMaxLevelModel,
            PlanetManager.Planet.Pluto => plutoMaxLevelModel,
            _ => null
        };

        // Fall back to the generic max-level entry from Level Models if
        // this planet doesn't have its own dedicated model assigned yet.
        if (model == null && levelModels != null && levelModels.Length > 0)
            model = levelModels[levelModels.Length - 1];

        return model;
    }

    bool IsMaxLevel(int checkedLevel) => levelModels != null && checkedLevel == levelModels.Length;

    public void SetLevel(int newLevel, long capacity)
    {
        level = newLevel;
        bool isBase = (newLevel <= 1);

        foreach (Renderer r in baseRenderers)
            if (r != null) r.enabled = isBase;

        // Every level EXCEPT the max one uses its fixed model from
        // Level Models, same as before. The max level instead picks from
        // the planet-specific set — deactivate the old generic max-level
        // entry (if any) and every planet's own max-level model first,
        // then activate only the correct one for wherever the player
        // currently is.
        for (int i = 0; i < levelModels.Length; i++)
        {
            bool isMaxLevelSlot = (i == levelModels.Length - 1);
            if (levelModels[i] != null)
                levelModels[i].SetActive(!isMaxLevelSlot && i == newLevel - 1);
        }

        foreach (GameObject m in AllMaxLevelModels())
            if (m != null) m.SetActive(false);

        if (IsMaxLevel(newLevel))
        {
            PlanetManager.Planet current = PlanetManager.Instance != null ? PlanetManager.Instance.currentPlanet : PlanetManager.Planet.Earth;
            GameObject model = MaxLevelModelForPlanet(current);
            if (model != null) model.SetActive(true);
        }

        SetCapacity(capacity);
    }

    // Called by PlanetManager whenever the active planet changes — if this
    // hut is ALREADY at max level, swap which planet's model is showing
    // without touching level/capacity at all. No-op for huts below max
    // level, since those use the same model on every planet anyway.
    public void RefreshPlanetVisual(PlanetManager.Planet planet)
    {
        if (!IsMaxLevel(level)) return;

        foreach (GameObject m in AllMaxLevelModels())
            if (m != null) m.SetActive(false);

        GameObject model = MaxLevelModelForPlanet(planet);
        if (model != null) model.SetActive(true);
    }

    public void SetCapacity(long capacity)
    {
        if (hut == null) return;
        hut.maxCapacity = capacity;
        // Re-clamp population against the new capacity right away — a
        // pure safety net (population should never legitimately exceed
        // capacity given RabbitHut's own internal clamps, but this
        // guarantees it structurally can't happen here either, regardless
        // of exact frame-timing between a level-up and a reproduction tick).
        hut.SetCurrentRabbits(hut.GetCurrentRabbits());
    }
}
