using Microsoft.VisualStudio.Extensibility.UI;

namespace Soffritto.Vsix.UI;

internal sealed class StatusControl(StatusViewModel dataContext) : RemoteUserControl(dataContext);
