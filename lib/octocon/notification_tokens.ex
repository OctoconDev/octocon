defmodule Octocon.NotificationTokens do
  @moduledoc """
  The NotificationTokens context.
  """

  import Ecto.Query, warn: false

  alias Octocon.{
    Accounts,
    Alters,
    Alters.Alter,
    Friendships.Friendship,
    NotificationTokens.NotificationToken,
    Repo
  }

  @doc """
  Gets a single notification_token.

  Raises `Ecto.NoResultsError` if the Notification token does not exist.

  ## Examples

      iex> get_notification_token!(123)
      %NotificationToken{}

      iex> get_notification_token!(456)
      ** (Ecto.NoResultsError)

  """
  def get_notification_tokens(system_identity) do
    user_id = Accounts.id_from_system_identity(system_identity, :system)

    from(n in NotificationToken,
      where: n.user_id == ^user_id,
      select: n
    )
    |> Repo.all_global()
  end

  def batch_notifications(system_identity, alter_ids) do
    user_id = Accounts.id_from_system_identity(system_identity, :system)

    # Load all alters belonging to the user and filtered by alter_ids
    alters =
      from(
        a in Alter,
        where: a.user_id == ^user_id and a.id in ^alter_ids,
        select: a
      )
      |> Repo.all_regional({:user, system_identity})

    # Load all friendships for the user
    friendships =
      from(
        f in Friendship,
        where: f.user_id == ^user_id,
        select: {f.friend_id, f.level}
      )
      |> Repo.all_global()

    friend_ids = Enum.map(friendships, fn {id, _level} -> id end)

    # Load all notification tokens for those friends
    tokens =
      from(
        n in NotificationToken,
        where: n.user_id in ^friend_ids,
        select: n
      )
      |> Repo.all_global()

    # Group tokens by user_id
    token_map = Enum.group_by(tokens, & &1.user_id)

    # Build final notification map
    friendships
    |> Enum.map(fn {friend_id, level} ->
      # Get tokens for this friend
      tokens = Map.get(token_map, friend_id, []) |> Enum.map(& &1.push_token)

      # Filter alters visible at this security level
      visible_alters =
        alters
        |> Enum.filter(fn alter ->
          Alters.can_view_entity?(level, alter.security_level)
        end)
        |> Enum.map_join(", ", & &1.name)

      visible_alters =
        cond do
          visible_alters == "" -> "No one is fronting"
          String.length(visible_alters) > 150 -> String.slice(visible_alters, 0..150) <> "\n..."
          true -> visible_alters
        end

      {tokens, visible_alters}
    end)
    |> Enum.reject(fn {tokens, _} -> tokens == [] end)
    |> Enum.into(%{})
  end

  def get_tokens_for_users(system_identities) do
    user_ids = Enum.map(system_identities, &Accounts.id_from_system_identity(&1, :system))

    from(n in NotificationToken,
      where: n.user_id in ^user_ids,
      select: n
    )
    |> Repo.all_global()
    |> Enum.group_by(& &1.user_id)
  end

  def add_notification_token(system_identity, token) do
    user_id = Accounts.id_from_system_identity(system_identity, :system)

    changeset =
      NotificationToken.changeset(%NotificationToken{}, %{user_id: user_id, push_token: token})

    Repo.insert_global(changeset)
  end

  def invalidate_notification_token(system_identity, token) do
    user_id = Accounts.id_from_system_identity(system_identity, :system)

    from(n in NotificationToken,
      where: n.user_id == ^user_id and n.push_token == ^token
    )
    |> Repo.delete_all_global()
  end

  def invalidate_notification_token(token) do
    from(n in NotificationToken,
      where: n.push_token == ^token
    )
    |> Repo.delete_all_global()
  end
end
