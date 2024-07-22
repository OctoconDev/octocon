defmodule OctoconDiscord.Emojis do
  @moduledoc false

  alias Nostrum.Struct.Emoji

  def one, do: %Emoji{name: "number1", id: 1_255_764_961_020_350_534}
  def two, do: %Emoji{name: "number2", id: 1_255_765_058_387_181_588}
  def three, do: %Emoji{name: "number3", id: 1_255_764_964_568_862_721}
  def four, do: %Emoji{name: "number4", id: 1_255_764_946_399_006_751}
  def five, do: %Emoji{name: "number5", id: 1_255_764_947_976_327_270}
  def six, do: %Emoji{name: "number6", id: 1_255_764_949_179_830_414}
  def seven, do: %Emoji{name: "number7", id: 1_255_764_950_060_634_172}
  def eight, do: %Emoji{name: "number8", id: 1_255_764_951_218_393_109}
  def nine, do: %Emoji{name: "number9", id: 1_255_764_952_149_659_658}
  def resources, do: %Emoji{name: "resources", id: 1_255_765_008_676_032_542}
  def faq, do: %Emoji{name: "faq", id: 1_255_765_434_872_107_018}
  def slashcommand, do: %Emoji{name: "slashcommand", id: 1_255_764_957_375_758_388}
  def folder, do: %Emoji{name: "folder", id: 1_255_840_880_980_529_152}
  def backarrow, do: %Emoji{name: "backarrow", id: 1256_277_935_778_828_448}

  def component_emoji(%Emoji{name: name, id: id}) do
    %{name: name, id: id}
  end
end
