defmodule OctoconDiscord do
  @moduledoc false

  def get_desired_shards() do
    Nostrum.Util.gateway() |> elem(1)
  end
end
