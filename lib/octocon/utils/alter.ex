defmodule Octocon.Utils.Alter do
  @moduledoc """
  Utility functions for working with alters.
  """

  alias Octocon.{
    Alters,
    Utils,
    Accounts
  }

  alias OctoconWeb.Uploaders.Avatar

  @doc """
  Uploads an avatar for the given alter to the Octocon CDN.

  ## Arguments

  - `system_identity` (tuple): The identity of the system to upload the avatar for.
  - `alter_identity` (tuple): The identity of the alter to upload the avatar for.
  - `url` (binary): The URL of the avatar to download and re-upload to the Octocon CDN.
  """
  def upload_avatar(system_identity, alter_identity, url) do
    random_id = Nanoid.generate(30)
    system_id = Accounts.id_from_system_identity(system_identity, :system)
    alter_id = Alters.resolve_alter({:system, system_id}, alter_identity)

    avatar_scope = %{
      system_id: system_id,
      alter_id: alter_id,
      random_id: random_id
    }

    Utils.nuke_existing_avatars!(system_id, alter_id)
    result = Avatar.store({url, avatar_scope})

    case result do
      {:ok, _} ->
        avatar_url = Avatar.url({"primary.webp", avatar_scope}, :primary)

        Alters.update_alter(
          {:system, system_id},
          {:id, alter_id},
          %{avatar_url: avatar_url}
        )

      _ ->
        {:error, :unknown}
    end
  end
end
