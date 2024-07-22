defmodule Octocon.NotificationTokens do
  @moduledoc """
  The NotificationTokens context.
  """

  import Ecto.Query, warn: false
  alias Octocon.Accounts
  alias Octocon.Alters
  alias Octocon.Alters.Alter
  alias Octocon.Friendships.Friendship
  alias Octocon.Repo

  alias Octocon.NotificationTokens.NotificationToken

  @doc """
  Gets a single notification_token.

  Raises `Ecto.NoResultsError` if the Notification token does not exist.

  ## Examples

      iex> get_notification_token!(123)
      %NotificationToken{}

      iex> get_notification_token!(456)
      ** (Ecto.NoResultsError)

  """
  def get_notification_tokens(user_identity) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    from(n in NotificationToken,
      where: n.user_id == ^user_id,
      select: n
    )
    |> Repo.all()
  end

  def batch_notifications(user_identity, alter_ids) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    alters_query =
      from(
        a in Alter,
        where: a.user_id == ^user_id and a.id in ^alter_ids,
        select: a
      )

    friends_query =
      from(
        f in Friendship,
        where: f.user_id == ^user_id,
        # Get notification tokens
        join: n in NotificationToken,
        on: n.user_id == f.friend_id,
        select: {f.friend_id, f.level, n}
      )

    alters = Repo.all(alters_query)

    Repo.all(friends_query)
    |> Enum.group_by(&elem(&1, 0))
    |> Enum.map(fn {id, list} ->
      {id,
       {
         hd(list) |> elem(1),
         Enum.reduce(list, [], fn {_, _, token}, acc ->
           [token.token | acc]
         end)
       }}
    end)
    |> Enum.map(fn {_id, {level, tokens}} ->
      visible_alters =
        alters
        |> Enum.filter(fn alter ->
          Alters.can_view_entity?(level, alter.security_level)
        end)
        |> Enum.map_join(", ", & &1.name)
        |> then(fn alters ->
          case alters do
            "" ->
              "No one is fronting"

            alters ->
              case String.length(alters) do
                length when length > 150 ->
                  alters
                  |> String.slice(0..150)
                  |> Kernel.<>("\n...")

                _ ->
                  alters
              end
          end
        end)

      {tokens, visible_alters}
    end)
    |> Enum.into(%{})
  end

  def get_tokens_for_users(user_identities) do
    user_ids = Enum.map(user_identities, &Accounts.id_from_system_identity(&1, :system))

    from(n in NotificationToken,
      where: n.user_id in ^user_ids,
      select: n
    )
    |> Repo.all()
    |> Enum.group_by(& &1.user_id)
  end

  def add_notification_token(user_identity, token) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    changeset =
      NotificationToken.changeset(%NotificationToken{}, %{user_id: user_id, token: token})

    Repo.insert(changeset, on_conflict: :nothing)
  end

  def invalidate_notification_token(user_identity, token) do
    user_id = Accounts.id_from_system_identity(user_identity, :system)

    from(n in NotificationToken,
      where: n.user_id == ^user_id and n.token == ^token
    )
    |> Repo.delete_all()
  end
end
