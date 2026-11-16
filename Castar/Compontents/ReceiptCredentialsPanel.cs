using Castar.Api;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Castar.Compontents;

internal sealed class ReceiptCredentialsPanel : StackPanel
{
    private readonly List<(ReceiptCredentialFieldDto Field, Control Input)> inputs = [];

    public ReceiptCredentialsPanel(IEnumerable<ReceiptCredentialFieldDto> fields)
    {
        MinWidth = 280;
        foreach (var field in fields)
        {
            var label = new TextBlock { Text = field.Label };
            label.SetResourceReference(StyleProperty, "FormFieldLabel");
            Control input = field.Secret ? new PasswordBox() : new TextBox();
            AutomationProperties.SetLabeledBy(input, label);
            AutomationProperties.SetIsRequiredForForm(input, field.Required);
            Children.Add(label);
            Children.Add(input);
            inputs.Add((field, input));
        }
    }

    public Dictionary<string, string> Values { get; private set; } = [];

    public ValidatePopupDialog Validate()
    {
        var values = new Dictionary<string, string>();
        foreach (var (field, input) in inputs)
        {
            var value = input is PasswordBox secret ? secret.Password : ((TextBox)input).Text.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                    return new(false, $"Заполните поле «{field.Label}».");
                continue;
            }
            values.Add(field.Key, value);
        }
        Values = values;
        return new(true, null);
    }
}
