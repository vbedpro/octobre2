using UnityEngine;
using UnityEngine.UI;
using TMPro;

// One entry in the Guild History scrollable list — an icon, a short
// description, and a date (day + year).
public class GuildHistoryEventRowUI : MonoBehaviour
{
    public Image icon;
    public TextMeshProUGUI descriptionText;
    public TextMeshProUGUI dateText;

    public void Set(Sprite eventIcon, string description, System.DateTime date)
    {
        if (icon != null) icon.sprite = eventIcon;
        if (descriptionText != null) descriptionText.text = description;
        if (dateText != null) dateText.text = date.ToString("MMM d, yyyy"); // day + year, e.g. "Aug 15, 2026"
    }
}
