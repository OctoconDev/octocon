defmodule Octocon.Utils.User do
  @moduledoc """
  Utility functions for working with users.
  """

  alias Octocon.{
    Accounts,
    Utils
  }

  alias OctoconWeb.Uploaders.UserAvatar

  @doc """
  Uploads an avatar for the given system to the Octocon CDN.

  ## Arguments

  - `system` (struct): The system to upload the avatar for.
  - `url` (binary): The URL of the avatar to download and re-upload to the Octocon CDN.
  """
  def upload_avatar(system, url) do
    random_id = Nanoid.generate(30)

    avatar_scope = %{
      system_id: system.id,
      random_id: random_id
    }

    Utils.nuke_existing_avatars!(system.id, "self")

    result = UserAvatar.store({url, avatar_scope})

    case result do
      {:ok, _} ->
        avatar_url = UserAvatar.url({"primary.webp", avatar_scope}, :primary)

        Accounts.update_user(system, %{avatar_url: avatar_url})

      _ ->
        {:error, :unknown}
    end
  end
end
