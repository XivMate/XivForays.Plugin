using System.Text;
using Dalamud.Bindings.ImGui;
namespace XivMate.DataGathering.Forays.Dalamud.Extensions;

/// <summary>
/// Helper methods for ImGui operations
/// </summary>
public static class ImGuiHelper
{
    /// <summary>
    /// Creates a labeled text input field
    /// </summary>
    /// <param name="label">Label to display next to the input</param>
    /// <param name="text">Reference to the text value</param>
    /// <param name="bufferSize">Size of the input buffer</param>
    /// <returns>True if the value was changed</returns>
    public static bool InputText(string label, ref string text, uint bufferSize = 256)
    {
        // Create buffer from current text value
        var buffer = Encoding.UTF8.GetBytes(text);
        if (buffer.Length > bufferSize - 1)
        {
            buffer = Encoding.UTF8.GetBytes(text.Substring(0, (int)bufferSize - 1));
        }

        var inputTextBuffer = new byte[bufferSize];
        System.Buffer.BlockCopy(buffer, 0, inputTextBuffer, 0, buffer.Length);

        // Display label with proper width
        ImGui.PushItemWidth(ImGui.CalcTextSize(label).X + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.TextUnformatted(label);
        ImGui.PopItemWidth();
        ImGui.SameLine();

        // Display input field and handle changes
        if (ImGui.InputText($"##{label}", inputTextBuffer))
        {
            text = Encoding.UTF8.GetString(inputTextBuffer).TrimEnd('\0'); // Remove trailing null chars
            return true;
        }
        return false;
    }
}
